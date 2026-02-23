# EasyMultiplayer 设计文档

> Godot 4.x + C# 纯网络层轻量插件 · `addons/EasyMultiplayer/`

---

## 1. 概述与目标

EasyMultiplayer 从千棋世界项目的网络代码中提炼而来，目标是提供一个通用的局域网多人游戏插件。

**核心原则：**

- 纯网络层，不含 UI — 插件只管连接、发现、心跳、重连，业务逻辑由使用者实现
- 不依赖外部 EventBus — 所有事件通过插件自身的 Godot Signal 暴露
- 传输层与发现层可插拔 — 通过 `ITransport` / `IDiscovery` 接口抽象，便于未来扩展 WebSocket、Steam 等
- 支持 2~N 人 — `MaxClients` 可配置，不再硬编码为 1

**与千棋世界的关键差异：**

| 方面 | 千棋世界 | EasyMultiplayer |
|------|---------|-----------------|
| 耦合 | NetworkManager 直接包含走子/悔棋等业务 RPC | 通用消息通道，插件不解析业务内容 |
| 事件 | 依赖全局 EventBus | 插件自身 Signal |
| 传输 | 硬编码 ENet | ITransport 接口，ENet 为默认实现 |
| 发现 | 硬编码 UDP 广播 | IDiscovery 接口，UDP 广播为默认实现 |
| 玩家数 | 固定 2 人 | 可配置 MaxClients |

---

## 2. 架构总览

```
┌─────────────────────────────────────────────┐
│              Application Layer              │
│         （使用者的游戏逻辑代码）               │
└──────────────────┬──────────────────────────┘
                   │ Signal / API
┌──────────────────▼──────────────────────────┐
│          EasyMultiplayer (Autoload)          │
│  统一入口：连接管理 · 房间 · 心跳 · 消息通道   │
├─────────────┬───────────────┬───────────────┤
│ RoomHost    │ RoomClient    │ MessageChannel│
├─────────────┴───────────────┴───────────────┤
│         Transport Abstraction               │
│  ITransport          IDiscovery             │
├──────────────────┬──────────────────────────┤
│  ENetTransport   │  UdpBroadcastDiscovery   │
└──────────────────┴──────────────────────────┘
```

**设计决策 — 为什么用 Autoload 单例？**
千棋世界的 NetworkManager 已验证 Autoload 模式在 Godot 中管理网络生命周期的可靠性。单例保证全局唯一的连接状态，避免多实例冲突。

---

## 3. ITransport 接口设计

```csharp
namespace EasyMultiplayer.Transport;

/// <summary>传输层抽象接口，所有网络传输实现此接口。</summary>
public interface ITransport
{
    // ── 生命周期 ──
    Error CreateHost(int port, int maxClients);
    Error CreateClient(string address, int port);
    void Disconnect();
    void DisconnectPeer(int peerId);
    void Poll();  // 每帧调用，驱动内部事件

    // ── 数据发送 ──
    void SendReliable(int peerId, int channel, byte[] data);
    void SendUnreliable(int peerId, int channel, byte[] data);

    // ── 属性 ──
    bool IsServer { get; }
    int UniqueId { get; }
    TransportStatus Status { get; }

    // ── 事件回调（由实现者触发） ──
    event Action<int> PeerConnected;       // peerId
    event Action<int> PeerDisconnected;    // peerId
    event Action<int, int, byte[]> DataReceived;  // peerId, channel, data
    event Action ConnectionSucceeded;
    event Action ConnectionFailed;
}

public enum TransportStatus
{
    Disconnected, Connecting, Connected
}
```

**参数说明：**

| 参数 | 类型 | 说明 |
|------|------|------|
| port | int | 监听/连接端口，默认 27015 |
| maxClients | int | 最大客户端数，默认 1 |
| address | string | 目标主机 IP |
| peerId | int | 对端标识符 |
| channel | int | 逻辑通道编号（0=默认） |
| data | byte[] | 原始数据载荷 |

**Rationale — 为什么不直接暴露 Godot MultiplayerPeer？**
直接暴露会将插件绑死在 Godot 的 Multiplayer 框架上。通过接口抽象，未来可以实现 WebSocket、Steam Networking 等传输层而不改动上层代码。`Poll()` 方法让非 Godot 原生的传输实现也能在 `_Process` 中被驱动。

### 3.1 ENetTransport 实现架构

ENetTransport 是 ITransport 的默认实现，基于 `ENetMultiplayerPeer`，但**不设置 MultiplayerPeer 到 MultiplayerAPI**。

**核心设计决策：**

| 问题 | 解决方案 |
|------|---------|
| 不设置 MultiplayerPeer 到 MultiplayerAPI | 避免 SceneMultiplayer 抢先消费数据包，避免 RPC 解析错误 |
| 客户端连接检测 | `Poll()` 中轮询 `GetConnectionStatus()` 状态变化（Connecting → Connected） |
| 服务器端 peer 发现 | 客户端连接成功后发送握手包（HandshakeChannel），服务器在数据包循环中发现新 senderId |
| 断开检测 | 客户端：轮询状态变为 Disconnected；服务器：ENet 信号 + 整体状态兜底检查 |
| 数据包读取顺序 | 先 `GetPacketPeer()` 再 `GetPacket()`，顺序不可颠倒 |
| 发包 | 直接 `PutPacket`，检查返回值，不检查 `_knownPeers` |

**Peer 发现流程（服务器端）：**

```
客户端连接成功
    │
    ├─► 发送握手包 SendReliable(1, HandshakeChannel, {0x01})
    │
服务器 Poll() 循环
    │
    ├─► 收到握手包（HandshakeChannel）
    │       └─ 若 senderId 未知 → _knownPeers.Add(senderId) + PeerConnected?.Invoke(senderId)
    │           握手包不传递给上层
    │
    └─► 收到普通数据包
            └─ 若 senderId 未知（兜底）→ PeerConnected?.Invoke(senderId)
                普通数据包传递给上层 DataReceived
```

**重连支持：**

`ENetTransport` 保存 `LastAddress` 和 `LastPort`，供 `HeartbeatManager` 在客户端自动重连时使用。

---

## 4. IDiscovery 接口设计

```csharp
namespace EasyMultiplayer.Discovery;

/// <summary>房间发现层抽象接口。</summary>
public interface IDiscovery
{
    // ── 广播端（Host） ──
    void StartBroadcast(RoomInfo info);
    void StopBroadcast();
    bool IsBroadcasting { get; }

    // ── 监听端（Client） ──
    void StartListening();
    void StopListening();
    bool IsListening { get; }
    IReadOnlyDictionary<string, DiscoveredRoom> Rooms { get; }

    // ── 事件 ──
    event Action<DiscoveredRoom> RoomFound;
    event Action<string> RoomLost;       // key = "ip:port"
    event Action RoomListUpdated;
}

/// <summary>房间信息，广播载荷。</summary>
public class RoomInfo
{
    public string Magic { get; set; } = "EASYMULTI_V1";
    public string HostName { get; set; } = "";
    public string GameType { get; set; } = "";
    public int PlayerCount { get; set; } = 1;
    public int MaxPlayers { get; set; } = 2;
    public int Port { get; set; } = 27015;
    public string Version { get; set; } = "1.0.0";
    public string InstanceId { get; set; } = "";  // 进程唯一标识，用于过滤自身广播
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>发现的房间条目。</summary>
public class DiscoveredRoom
{
    public RoomInfo Info { get; set; } = new();
    public string HostIp { get; set; } = "";
    public double LastSeen { get; set; }
}
```

**与千棋世界的改进：**
- `RoomInfo` 新增 `Metadata` 字典，使用者可存放自定义数据（如游戏模式、地图名）而无需修改插件
- `RoomInfo` 新增 `InstanceId` 字段，每个进程启动时生成随机 ID，用于过滤自身广播（替代 IP 过滤，支持同机多实例测试）
- Magic 改为 `EASYMULTI_V1`，避免与千棋世界广播冲突
- 超时时间、广播间隔从硬编码改为通过 `EasyMultiplayerConfig` 配置

### 4.1 UdpBroadcastDiscovery 实现细节

**自身广播过滤：**

使用 `InstanceId`（进程启动时生成的随机 8 位 hex 字符串）而非 IP 地址过滤自身广播。这样同一台机器上的两个实例可以互相发现对方的房间，支持本地多实例测试。

```
实例 A（InstanceId="a1b2c3d4"）广播 → 实例 B 收到
实例 B 检查：info.InstanceId != 自身 InstanceId → 接受
实例 A 收到自己的广播 → info.InstanceId == 自身 InstanceId → 过滤
```

**线程安全：**

UDP 接收在后台线程执行，通过 `CallDeferred` 将 `HandleRoomFound` 切回主线程处理，确保 Godot 对象访问的线程安全。

**本机 IP 预加载：**

`PreloadLocalIps()` 在 `_Ready` 时异步预加载本机 IP（用于历史兼容），实际自身过滤已改用 InstanceId，IP 集合仅作备用。

---

## 5. 连接管理与状态机

### 5.1 状态枚举

```csharp
public enum ConnectionState
{
    Disconnected,   // 初始/已断开
    Hosting,        // 作为主机等待连接
    Joining,        // 客户端正在连接
    Connected,      // 已连接（双方在线）
    Reconnecting    // 对端断线，等待重连
}
```

### 5.2 状态转换图

```
                  Host(port)              Join(addr,port)
 ┌──────────┐ ──────────────► ┌─────────┐     ┌─────────┐
 │Disconnected│               │ Hosting │     │ Joining │
 └─────▲──────┘ ◄──────────── └────┬────┘     └────┬────┘
       │         Disconnect()      │ PeerJoined    │ ConnSucceeded
       │                           ▼               ▼
       │                     ┌──────────┐
       │  Disconnect()       │Connected │
       │◄──────────────────  └────┬─────┘
       │                          │ PeerTimeout / PeerDisconnected
       │                          ▼
       │  ReconnectTimeout   ┌────────────┐
       │◄──────────────────  │Reconnecting│
       │  / ReconnectFailed  └────────────┘
       │                          │ PeerReconnected
       │                          ▼
       │                     ┌──────────┐
       │                     │Connected │ (恢复)
       │                     └──────────┘
```

### 5.3 守卫条件

| 转换 | 前置条件 |
|------|---------|
| Disconnected → Hosting | 当前必须为 Disconnected |
| Disconnected → Joining | 当前必须为 Disconnected |
| Hosting → Connected | 收到 PeerConnected 事件 |
| Joining → Connected | 收到 ConnectionSucceeded |
| Connected → Reconnecting | 心跳超时或 PeerDisconnected（非主动退出） |
| Reconnecting → Connected | 对端重新连接成功 |
| Reconnecting → Disconnected | 重连超时或用户取消 |
| Any → Disconnected | 调用 Disconnect() |

### 5.4 核心信号

```csharp
[Signal] delegate void StateChangedEventHandler(int oldState, int newState);
[Signal] delegate void PeerJoinedEventHandler(int peerId);
[Signal] delegate void PeerLeftEventHandler(int peerId);
[Signal] delegate void ConnectionSucceededEventHandler();
[Signal] delegate void ConnectionFailedEventHandler();
```

---

## 6. 房间系统

### 6.1 RoomHost 状态机

```
 ┌──────┐  CreateRoom()  ┌─────────┐  PeerJoined  ┌───────┐
 │ Idle │ ──────────────► │ Waiting │ ────────────► │ Ready │
 └──────┘                 └─────────┘               └───┬───┘
    ▲                         ▲                         │
    │ CloseRoom()             │ PeerLeft                │ StartGame()
    │◄────────────────────────┤◄────────────────────    ▼
    │                         │                    ┌─────────┐
    │◄─────────────────────────────────────────────│ Playing │
    │              CloseRoom()                     └─────────┘
```

```csharp
public partial class RoomHost : Node
{
    // ── 状态 ──
    public RoomState State { get; }
    public string RoomName { get; }
    public string GameType { get; }
    public int Port { get; }
    public int GuestPeerId { get; }
    public bool HostReady { get; }
    public bool GuestReady { get; }

    // ── API ──
    public Error CreateRoom(string name, string gameType, int port);
    public void CloseRoom();
    public void SetHostReady(bool ready);
    public void StartGame();
    public void ResetReadyState();

    // ── 信号 ──
    [Signal] delegate void RoomStateChangedEventHandler(int old, int next);
    [Signal] delegate void GuestJoinedEventHandler(int peerId);
    [Signal] delegate void GuestLeftEventHandler(int peerId);
    [Signal] delegate void GuestReadyChangedEventHandler(int peerId, bool ready);
    [Signal] delegate void AllReadyEventHandler();
    [Signal] delegate void GameStartingEventHandler(string gameType);
}
```

### 6.2 RoomClient 状态机

```
 ┌──────┐ StartSearching() ┌───────────┐ JoinRoom() ┌─────────┐
 │ Idle │ ────────────────► │ Searching │ ──────────► │ Joining │
 └──────┘                   └───────────┘             └────┬────┘
    ▲                                                      │
    │ LeaveRoom() / Disconnected                           │ ConnSucceeded
    │◄─────────────────────────────────────────────────    ▼
    │                                                 ┌────────┐
    │◄────────────────────────────────────────────────│ InRoom │
    │              LeaveRoom()                        └───┬────┘
    │                                                     │ GameStart
    │                                                     ▼
    │                                              ┌──────────────┐
    │◄─────────────────────────────────────────────│ GameStarting │
    │                                              └──────────────┘
```

```csharp
public partial class RoomClient : Node
{
    public ClientState State { get; }
    public RoomInfo? CurrentRoom { get; }
    public bool IsReady { get; }
    public bool HostReady { get; }

    public void StartSearching();
    public void StopSearching();
    public Error JoinRoom(string hostIp, int port);
    public void LeaveRoom();
    public void SetReady(bool ready);
    public IReadOnlyDictionary<string, DiscoveredRoom>? GetDiscoveredRooms();

    [Signal] delegate void ClientStateChangedEventHandler(int old, int next);
    [Signal] delegate void JoinSucceededEventHandler(string roomName, string gameType);
    [Signal] delegate void JoinFailedEventHandler(string reason);
    [Signal] delegate void HostReadyChangedEventHandler(bool ready);
    [Signal] delegate void GameStartingEventHandler(string gameType);
    [Signal] delegate void DisconnectedFromRoomEventHandler(string reason);
}
```

**Rationale — 为什么 RoomHost/RoomClient 独立于 EasyMultiplayer 单例？**
房间逻辑是可选的。有些游戏可能不需要房间概念（如直连 IP），将其独立为节点让使用者按需实例化，保持单例轻量。

---

## 7. 心跳检测与网络质量

### 7.1 机制

```
 Host/Client                    Remote
     │                            │
     │──── Ping (Unreliable) ────►│
     │                            │
     │◄─── Pong (Unreliable) ─────│
     │                            │
     │  RTT = Pong收到时间 - Ping发送时间
     │  若超过 DisconnectTimeout 未收到 Pong → 触发断线
```

### 7.2 配置参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| HeartbeatInterval | 3.0s | Ping 发送间隔 |
| DisconnectTimeout | 10.0s | 判定断线的超时阈值 |

### 7.3 网络质量分级

```csharp
public enum NetQuality
{
    Good,       // RTT < 100ms
    Warning,    // 100ms ≤ RTT < 300ms
    Bad         // RTT ≥ 300ms 或断线
}
```

### 7.4 信号

```csharp
[Signal] delegate void NetQualityChangedEventHandler(int quality, double rttMs);
[Signal] delegate void PeerTimedOutEventHandler(int peerId);
```

**Rationale — 为什么用 Unreliable 发心跳？**
心跳包丢失不需要重传，用 Unreliable 避免阻塞可靠通道。丢一两个 Ping 不影响判定，因为超时阈值远大于发送间隔（10s vs 3s，至少容忍连续 3 次丢包）。

---

## 8. 断线重连

### 8.1 Host 端流程

```
 Connected ──(心跳超时/PeerDisconnected)──► Reconnecting
     │                                          │
     │                                          │ 等待对端重连
     │                                          │ 计时 ReconnectTimeout
     │                                          │
     │  ◄──(PeerConnected)── 重连成功            │
     │  → 恢复 Connected，重启心跳               │
     │                                          │
     │                          超时 ──► ReconnectTimedOut 信号
     │                                  → Disconnected
```

### 8.2 Client 端自动重连

```
 ServerDisconnected
     │
     ▼
 StartClientAutoReconnect()
     │
     ├─► 尝试 CreateClient(lastAddr, lastPort)
     │       │
     │       ├─ 成功 → Connected，请求全量同步
     │       │
     │       └─ 失败 → attempts++
     │               │
     │               ├─ attempts < Max → 等待 RetryInterval，重试
     │               │
     │               └─ attempts ≥ Max → ReconnectFailed
     │
     └─► 发出 OpponentDisconnected 信号（通知 UI）
```

### 8.3 配置参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| ReconnectTimeout | 30.0s | Host 端等待重连的上限 |
| MaxReconnectAttempts | 20 | Client 端最大重试次数 |
| ReconnectRetryInterval | 3.0s | Client 端每次重试间隔 |

### 8.4 重连信号

```csharp
[Signal] delegate void PeerReconnectedEventHandler(int peerId);
[Signal] delegate void ReconnectTimedOutEventHandler();
[Signal] delegate void ReconnectFailedEventHandler();
```

### 8.5 重连后状态同步

重连成功后，双方状态可能不一致。插件提供 `RequestFullSync` / `ReceiveFullSync` 信号对，由使用者实现具体的状态同步逻辑：

```csharp
// 插件在重连成功后自动发出此信号，使用者监听并发送全量状态
[Signal] delegate void FullSyncRequestedEventHandler(int peerId);
// 使用者通过 MessageChannel 发送同步数据，对端收到后自行恢复
```

**Rationale — 为什么 Client 端自动重连而 Host 端只等待？**
Host 端持有服务器 socket，地址不变，只需保持监听。Client 端需要主动发起连接，因此由 Client 驱动重连流程。这与千棋世界的实践一致，经过实际验证可靠。

**Rationale — 为什么不在插件内实现状态同步？**
状态同步的内容完全取决于业务（棋盘状态、游戏进度等），插件无法预知。插件只负责通知"需要同步了"，具体数据由使用者通过消息通道发送。

---

## 9. 版本校验

### 9.1 流程

连接建立后，Client 自动向 Host 发送本地版本号。Host 比对后决定放行或踢出。

```
 Client                          Host
   │  ── SendVersion(ver) ──────►│
   │                              │ 比对 ver == GameVersion?
   │                              │
   │  ◄── VersionVerified ───────│  (匹配)
   │                              │
   │  ◄── VersionMismatch ──────│  (不匹配，延迟 300ms 后踢出)
```

### 9.2 API

```csharp
public partial class EasyMultiplayer
{
    /// <summary>使用者设置的游戏版本号，连接时自动交换。</summary>
    public string GameVersion { get; set; } = "1.0.0";

    // ── 信号 ──
    [Signal] delegate void VersionVerifiedEventHandler(string remoteVersion);
    [Signal] delegate void VersionMismatchEventHandler(string localVersion, string remoteVersion);
}
```

### 9.3 参数说明

| 参数 | 类型 | 说明 |
|------|------|------|
| GameVersion | string | 语义化版本号，由使用者在启动时设置 |
| remoteVersion | string | 对端发来的版本号 |
| localVersion | string | 本机版本号 |

**Rationale — 为什么延迟踢出？**
先发送 `VersionMismatch` 消息再断开，让 Client 有机会收到不匹配原因并展示给玩家，而非直接断开导致玩家困惑。千棋世界的实现中使用 300ms 延迟，实测足够 RPC 送达。

---

## 10. 通用消息通道

### 10.1 设计目标

替代千棋世界中散落在 NetworkManager 里的业务 RPC（走子、悔棋、认输等），提供统一的消息收发接口。插件不解析消息内容，只负责投递。

### 10.2 API

```csharp
namespace EasyMultiplayer.Core;

public partial class MessageChannel : Node
{
    /// <summary>发送可靠消息。</summary>
    public void SendReliable(int peerId, string channel, byte[] data);

    /// <summary>发送可靠消息（string 重载）。</summary>
    public void SendReliable(int peerId, string channel, string data);

    /// <summary>发送不可靠消息（适合高频低优先数据）。</summary>
    public void SendUnreliable(int peerId, string channel, byte[] data);

    /// <summary>广播给所有已连接对端。</summary>
    public void Broadcast(string channel, byte[] data, bool reliable = true);

    // ── 信号 ──
    [Signal] delegate void MessageReceivedEventHandler(
        int peerId, string channel, byte[] data);
}
```

### 10.3 参数说明

| 参数 | 类型 | 说明 |
|------|------|------|
| peerId | int | 目标对端 ID，0 表示广播 |
| channel | string | 逻辑通道标识，如 `"move"`, `"chat"`, `"sync"` |
| data | byte[] / string | 消息载荷，插件不解析 |
| reliable | bool | 是否使用可靠传输，默认 true |

### 10.4 RPC 频率限制

```csharp
public partial class MessageChannel
{
    /// <summary>每通道最小发送间隔（毫秒），0 表示不限制。</summary>
    public double RpcMinIntervalMs { get; set; } = 100.0;
}
```

超过频率的消息会被静默丢弃并打印警告日志。

**Rationale — 为什么用 string channel 而非 int enum？**
string 对使用者更友好，无需维护枚举映射。局域网场景下字符串开销可忽略。使用者可自行定义常量类管理通道名。

---

## 11. 兜底机制（强制）

所有联机功能必须遵守以下兜底规则，确保玩家在任何异常情况下都不会"卡住"。

### 11.1 超时回退

所有等待状态必须有超时，超时后自动回退到安全状态：

| 等待状态 | 默认超时 | 回退目标 |
|---------|---------|---------|
| Joining（连接中） | 10s | Disconnected |
| Searching（搜索房间） | 无上限，但提供取消按钮 | Idle |
| Reconnecting（重连中） | 30s（Host）/ 60s（Client 含重试） | Disconnected |
| 版本校验等待 | 5s | 断开连接 |

### 11.2 主动退出通知

```csharp
public partial class EasyMultiplayer
{
    /// <summary>
    /// 主动退出时调用：先发通知再延迟断开，让对端区分主动退出与意外断线。
    /// </summary>
    public async void GracefulDisconnect(string reason = "quit")
    {
        SendGracefulQuit(reason);
        await ToSignal(GetTree().CreateTimer(0.2), "timeout");
        Disconnect();
    }

    [Signal] delegate void PeerGracefulQuitEventHandler(int peerId, string reason);
}
```

### 11.3 状态重置与信号清理

```csharp
// 每次进入新阶段时重置相关状态
public void ResetState()
{
    // 由各模块在状态转换时自动调用
}

// _ExitTree 时清理
public override void _ExitTree()
{
    Disconnect();
    // 断开所有信号连接，释放网络资源
}
```

### 11.4 设计规则清单

1. 连接丢失自动处理 — 所有联机界面必须处理连接丢失
2. 超时与取消 — 所有等待状态必须有超时 + 取消按钮
3. 主动退出通知 — 退出时先发 RPC 再延迟断开
4. 状态重置 — 进入新界面时重置所有状态变量
5. 信号断开 — 离开界面时正确断开信号连接
6. 区分退出类型 — 主动退出 → 立即通知；意外断线 → 超时判定

**Rationale — 为什么兜底是"强制"的？**
千棋世界开发中多次遇到玩家卡在等待界面的 bug，根因都是缺少超时或未处理异常断线。将兜底作为强制规则写入插件设计，从架构层面杜绝此类问题。

---

## 12. 配置与导出

### 12.1 EasyMultiplayerConfig

```csharp
namespace EasyMultiplayer.Core;

[GlobalClass]
public partial class EasyMultiplayerConfig : Resource
{
    // ── 连接 ──
    [Export] public int Port { get; set; } = 27015;
    [Export] public int MaxClients { get; set; } = 1;

    // ── 心跳 ──
    [Export] public float HeartbeatInterval { get; set; } = 3.0f;
    [Export] public float DisconnectTimeout { get; set; } = 10.0f;

    // ── 重连 ──
    [Export] public float ReconnectTimeout { get; set; } = 30.0f;
    [Export] public int MaxReconnectAttempts { get; set; } = 20;
    [Export] public float ReconnectRetryInterval { get; set; } = 3.0f;

    // ── 消息 ──
    [Export] public double RpcMinIntervalMs { get; set; } = 100.0;

    // ── 发现 ──
    [Export] public int BroadcastPort { get; set; } = 27016;
    [Export] public float BroadcastInterval { get; set; } = 1.0f;
    [Export] public double RoomTimeout { get; set; } = 5.0;
}
```

### 12.2 参数一览

| 参数 | 默认值 | 说明 |
|------|--------|------|
| Port | 27015 | ENet 监听/连接端口 |
| MaxClients | 1 | 最大客户端数 |
| HeartbeatInterval | 3.0s | 心跳发送间隔 |
| DisconnectTimeout | 10.0s | 心跳超时阈值 |
| ReconnectTimeout | 30.0s | Host 端重连等待上限 |
| MaxReconnectAttempts | 20 | Client 端最大重试次数 |
| ReconnectRetryInterval | 3.0s | Client 端重试间隔 |
| RpcMinIntervalMs | 100ms | 消息通道最小发送间隔 |
| BroadcastPort | 27016 | UDP 广播端口 |
| BroadcastInterval | 1.0s | 广播发送间隔 |
| RoomTimeout | 5.0s | 房间超时移除阈值 |

### 12.3 plugin.cfg

```ini
[plugin]
name="EasyMultiplayer"
description="Lightweight LAN multiplayer plugin for Godot 4.x + C#"
author="EasyMultiplayer"
version="1.0.0"
script="EasyMultiplayerPlugin.cs"
```

**Rationale — 为什么用 Resource 而非静态常量？**
Resource 支持 Inspector 编辑和 `.tres` 文件持久化，设计师和开发者可以在编辑器中直接调参，无需改代码重编译。同时也支持代码中动态修改。

---

## 13. 插件目录结构

```
addons/EasyMultiplayer/
├── plugin.cfg                            # 插件注册信息
├── EasyMultiplayerPlugin.cs              # EditorPlugin 入口，注册 Autoload
├── Core/
│   ├── EasyMultiplayer.cs                # Autoload 单例，统一 API 入口
│   ├── ConnectionState.cs                # ConnectionState 枚举
│   ├── EasyMultiplayerConfig.cs          # 配置 Resource（[Export] 参数）
│   └── MessageChannel.cs                 # 通用消息通道
├── Transport/
│   ├── ITransport.cs                     # 传输层接口
│   ├── TransportStatus.cs                # 传输状态枚举
│   └── ENetTransport.cs                  # ENet 默认实现
├── Discovery/
│   ├── IDiscovery.cs                     # 发现层接口
│   ├── RoomInfo.cs                       # 房间信息 + DiscoveredRoom
│   └── UdpBroadcastDiscovery.cs          # UDP 广播默认实现
├── Room/
│   ├── RoomHost.cs                       # 房间主机逻辑
│   ├── RoomClient.cs                     # 房间客户端逻辑
│   └── RoomState.cs                      # RoomState / ClientState 枚举
└── Heartbeat/
    └── HeartbeatManager.cs               # 心跳、RTT、网络质量、重连计时
```

**职责划分：**
- `Core/` — 插件核心：单例入口、配置、消息通道
- `Transport/` — 传输层抽象与实现，可替换
- `Discovery/` — 房间发现抽象与实现，可替换
- `Room/` — 房间生命周期管理，可选模块
- `Heartbeat/` — 心跳检测与网络质量监控

---

## 14. 信号一览表

### 14.1 连接模块（EasyMultiplayer）

| 信号 | 参数 | 触发时机 |
|------|------|---------|
| StateChanged | (int old, int next) | 连接状态发生转换 |
| PeerJoined | (int peerId) | 对端连接成功 |
| PeerLeft | (int peerId) | 对端断开连接 |
| ConnectionSucceeded | — | Client 连接 Host 成功 |
| ConnectionFailed | — | Client 连接 Host 失败 |
| VersionVerified | (string remoteVersion) | 版本校验通过 |
| VersionMismatch | (string localVer, string remoteVer) | 版本不匹配 |
| PeerGracefulQuit | (int peerId, string reason) | 对端主动退出 |

### 14.2 房间模块（RoomHost）

| 信号 | 参数 | 触发时机 |
|------|------|---------|
| RoomStateChanged | (int old, int next) | 房间状态转换 |
| GuestJoined | (int peerId) | 客人加入房间 |
| GuestLeft | (int peerId) | 客人离开房间 |
| GuestReadyChanged | (int peerId, bool ready) | 客人准备状态变更 |
| AllReady | — | 双方都已准备 |
| GameStarting | (string gameType) | 游戏即将开始 |

### 14.3 房间模块（RoomClient）

| 信号 | 参数 | 触发时机 |
|------|------|---------|
| ClientStateChanged | (int old, int next) | 客户端状态转换 |
| JoinSucceeded | (string roomName, string gameType) | 成功加入房间 |
| JoinFailed | (string reason) | 加入房间失败 |
| HostReadyChanged | (bool ready) | 房主准备状态变更 |
| GameStarting | (string gameType) | 收到游戏开始通知 |
| DisconnectedFromRoom | (string reason) | 与房间断开连接 |

### 14.4 心跳模块（HeartbeatManager）

| 信号 | 参数 | 触发时机 |
|------|------|---------|
| NetQualityChanged | (int quality, double rttMs) | 网络质量等级变化 |
| PeerTimedOut | (int peerId) | 对端心跳超时 |
| PeerReconnected | (int peerId) | 对端重连成功 |
| ReconnectTimedOut | — | 重连等待超时 |
| ReconnectFailed | — | Client 重连全部失败 |

### 14.5 消息模块（MessageChannel）

| 信号 | 参数 | 触发时机 |
|------|------|---------|
| MessageReceived | (int peerId, string channel, byte[] data) | 收到消息 |

### 14.6 同步模块

| 信号 | 参数 | 触发时机 |
|------|------|---------|
| FullSyncRequested | (int peerId) | 重连后需要全量同步 |

### 14.7 与千棋世界 EventBus 信号对照

| 千棋世界 EventBus 信号 | EasyMultiplayer 信号 |
|----------------------|---------------------|
| OpponentDisconnected | PeerTimedOut / PeerLeft |
| OpponentReconnected | PeerReconnected |
| ReconnectTimeout | ReconnectTimedOut |
| ConnectionLost | StateChanged → Disconnected |
| RemoteReadyReceived | GuestReadyChanged / HostReadyChanged |
| VersionMismatch | VersionMismatch |
| NetQualityChanged | NetQualityChanged |
| OpponentQuitGame | PeerGracefulQuit(reason="game") |
| OpponentLeftRoom | PeerGracefulQuit(reason="room") |
| FullSyncRequested | FullSyncRequested |

---

## 15. 迁移指南（从千棋世界到 EasyMultiplayer）

### 15.1 NetworkManager → EasyMultiplayer 单例

```csharp
// ── 千棋世界 ──
var nm = GetNode<NetworkManager>("/root/NetworkManager");
nm.CreateHost(27015);
nm.JoinHost("192.168.1.100", 27015);
nm.Disconnect();

// ── EasyMultiplayer ──
var em = GetNode<EasyMultiplayer>("/root/EasyMultiplayer");
em.Host(27015);
em.Join("192.168.1.100", 27015);
em.Disconnect();
```

### 15.2 EventBus 依赖 → 插件 Signal 直连

```csharp
// ── 千棋世界 ──
var eventBus = GetNode("/root/EventBus");
eventBus.Connect("OpponentDisconnected", Callable.From(OnOpponentDisconnected));
eventBus.Connect("RemoteMoveReceived", Callable.From<Vector2I, Vector2I, string>(OnMove));

// ── EasyMultiplayer ──
var em = GetNode<EasyMultiplayer>("/root/EasyMultiplayer");
em.PeerTimedOut += OnPeerTimedOut;
em.MessageChannel.MessageReceived += OnMessageReceived;
```

### 15.3 业务 RPC → 通用消息通道

```csharp
// ── 千棋世界：每种业务一个 RPC ──
networkManager.SendMove(from, to);
networkManager.SendUndo();
networkManager.SendResign();
networkManager.SendDrawOffer();

// ── EasyMultiplayer：统一消息通道 ──
var ch = em.MessageChannel;
ch.SendReliable(peerId, "move", Serialize(from, to));
ch.SendReliable(peerId, "undo", Array.Empty<byte>());
ch.SendReliable(peerId, "resign", Array.Empty<byte>());
ch.SendReliable(peerId, "draw_offer", Array.Empty<byte>());
```

### 15.4 RoomDiscovery → IDiscovery

```csharp
// ── 千棋世界：直接使用具体类 ──
var rd = GetNode<RoomDiscovery>("/root/RoomDiscovery");
rd.StartBroadcast(new RoomInfo { Magic = "QIANQI_V1", ... });
rd.StartListening();

// ── EasyMultiplayer：通过接口，Magic 自动设置 ──
var discovery = em.Discovery; // IDiscovery
discovery.StartBroadcast(new RoomInfo { HostName = "My Room", ... });
discovery.StartListening();
```

### 15.5 版本校验

```csharp
// ── 千棋世界：硬编码在 NetworkManager 内部 ──
public const string GameVersion = "0.1.0";
// 校验逻辑散落在 RPC 方法中

// ── EasyMultiplayer：使用者设置，插件自动校验 ──
em.GameVersion = "1.0.0";
em.VersionMismatch += (local, remote) => {
    GD.Print($"版本不匹配: {local} vs {remote}");
};
```

### 15.6 迁移检查清单

1. ✅ 移除 `EventBus` 网络相关信号，改用插件 Signal
2. ✅ 将 `NetworkManager` 的业务 RPC 迁移到 `MessageChannel`
3. ✅ 将 `RoomDiscovery` 替换为 `IDiscovery` 接口调用
4. ✅ 将硬编码配置迁移到 `EasyMultiplayerConfig` Resource
5. ✅ 将 `QIANQI_V1` Magic 替换为 `EASYMULTI_V1`
6. ✅ 确认所有兜底机制（超时、取消、主动退出通知）已就位

---

## 16. 重要注意事项

### 16.1 绝对不能设置 MultiplayerPeer 到 MultiplayerAPI

设置 `Multiplayer.MultiplayerPeer = _peer` 后，Godot 的 `SceneMultiplayer` 会在内部 poll 中消费数据包并尝试解析为 RPC，导致：

- `process_rpc: Invalid packet received` 错误
- 数据包被 SceneMultiplayer 抢先消费，`Poll()` 收不到数据

**ENetTransport 不设置 MultiplayerPeer 到 MultiplayerAPI，自己管理连接检测和数据包收发。**

注意：`ProcessPriority = int.MinValue` 无法解决此问题，因为 SceneMultiplayer 的 poll 不走 Node 的 `_Process`。

### 16.2 不依赖 ENetMultiplayerPeer 的 Godot 信号

`ENetMultiplayerPeer` 的 `PeerConnected` / `PeerDisconnected` 信号在不设置 MultiplayerPeer 到 MultiplayerAPI 时行为不稳定。

**ENetTransport 的 peer 发现机制：**

- 客户端连接检测：`Poll()` 中轮询 `GetConnectionStatus()` 变化
- 服务器端 peer 发现：客户端连接成功后发送握手包，服务器在数据包循环中发现新 senderId
- 断开检测：通过心跳超时（HeartbeatManager）+ ENet 信号兜底

### 16.3 数据包读取顺序

```csharp
// 正确：先读 peer ID，再取包
var senderId = (int)_peer.GetPacketPeer();
var rawPacket = _peer.GetPacket();

// 错误：GetPacket() 会移除队列中的包，之后 GetPacketPeer() 会报错
```

### 16.4 客户端 ConnectedPeers 必须手动添加 server peer

客户端连接成功后，server peer ID 固定为 1，需要手动添加到 `_connectedPeers`：

```csharp
private void OnTransportConnectionSucceeded()
{
    _connectedPeers.Add(1); // Server peer ID = 1，必须手动添加
    _heartbeat.TrackPeer(1);
    _heartbeat.Start();
}
```

### 16.5 UDP 广播自身过滤使用 InstanceId 而非 IP

同一台机器上两个实例的 IP 相同，用 IP 过滤会导致互相搜不到房间。

**使用 `RoomInfo.InstanceId`（每个进程启动时生成随机 ID）过滤自身广播。**

### 16.6 发包前检查连接状态

`ENetMultiplayerPeer.PutPacket` 在 C++ 层检查 peers 表，对不存在的 peer 发包会报 `Invalid target peer` 错误。

发包前检查 `Status == TransportStatus.Connected`，心跳等定期发送的模块也要检查连接状态。

---

## 17. 未来扩展（v2+）

### 17.1 WebSocketTransport

实现 `ITransport` 接口，用于跨网段或 Web 平台场景。适用于 Godot HTML5 导出。

### 17.2 SteamTransport

封装 Steam Networking Sockets，实现 `ITransport`。利用 Steam Relay 网络实现 NAT 穿透，无需玩家配置端口转发。

### 17.3 LobbyServerDiscovery

实现 `IDiscovery` 接口，通过中心化 HTTP/WebSocket 服务器管理房间列表。突破局域网限制，支持互联网匹配。

### 17.4 消息缓冲与重放

断线期间缓存未送达的消息，重连后按序重放。需要为每条消息添加序列号，接收端去重。标记为 v2 是因为局域网场景下重连速度快，缓冲需求不强。

### 17.5 NAT 穿透 / Relay

集成 STUN/TURN 或自建 Relay 服务器，让不在同一局域网的玩家也能直连。可作为 `ITransport` 的装饰器层实现，对上层透明。

### 17.6 扩展路线图

| 版本 | 特性 | 优先级 |
|------|------|--------|
| v1.0 | ENet + UDP 广播（本文档） | 🔴 核心 |
| v1.1 | 消息缓冲与重放 | 🟡 中等 |
| v2.0 | WebSocketTransport | 🟡 中等 |
| v2.0 | LobbyServerDiscovery | 🟡 中等 |
| v2.1 | SteamTransport | 🟢 低 |
| v2.2 | NAT 穿透 / Relay | 🟢 低 |
