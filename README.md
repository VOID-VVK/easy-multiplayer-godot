# EasyMultiplayer

Godot 4.x + C# 轻量局域网多人游戏插件。

---

## 简介

EasyMultiplayer 从千棋世界项目的网络代码中提炼而来，提供一套通用的局域网多人游戏基础设施。插件只管连接、发现、心跳、重连，业务逻辑由使用者实现。

**核心原则：**

- 纯网络层，不含 UI
- 不依赖外部 EventBus，所有事件通过插件自身的 Godot Signal 暴露
- 传输层与发现层可插拔（`ITransport` / `IDiscovery` 接口）
- 支持 2~N 人，`MaxClients` 可配置

---

## 特性

- **ENet 传输层** — 不设置 MultiplayerPeer 到 MultiplayerAPI，通过握手包和状态轮询驱动 peer 发现，避免 Godot RPC 系统干扰
- **UDP 广播发现** — 局域网房间自动发现，使用 InstanceId 过滤自身广播，支持同机多实例测试
- **心跳检测** — Ping/Pong 机制，RTT 计算，网络质量分级（Good / Warning / Bad）
- **断线重连** — Host 端等待重连，Client 端自动重试，重连后触发全量同步请求
- **版本校验** — 连接建立后自动交换版本号，不匹配时延迟踢出并通知原因
- **主动退出通知** — `GracefulDisconnect()` 先发通知再断开，让对端区分主动退出与意外断线
- **消息通道** — 统一的 string channel 消息收发，内置频率限制
- **房间系统** — RoomHost / RoomClient 管理房间生命周期和准备状态（可选模块）

---

## 快速上手

### 1. 安装插件

将 `addons/EasyMultiplayer/` 目录复制到你的项目中，在 **项目设置 → 插件** 中启用 `EasyMultiplayer`。

插件会自动注册 `EasyMultiplayer` Autoload 单例。

### 2. 配置

在 Inspector 中编辑 `EasyMultiplayer` 节点的 `Config` 属性，或在代码中动态修改：

```csharp
var em = GetNode<EasyMultiplayer>("/root/EasyMultiplayer");
em.GameVersion = "1.0.0";
em.Config.Port = 27015;
em.Config.MaxClients = 3;
```

### 3. 作为主机

```csharp
var em = GetNode<EasyMultiplayer>("/root/EasyMultiplayer");

em.PeerJoined += (peerId) => GD.Print($"玩家 {peerId} 加入");
em.PeerLeft += (peerId) => GD.Print($"玩家 {peerId} 离开");

em.Host(); // 使用配置中的端口
```

### 4. 作为客户端

```csharp
var em = GetNode<EasyMultiplayer>("/root/EasyMultiplayer");

em.ConnectionSucceeded += () => GD.Print("连接成功");
em.ConnectionFailed += () => GD.Print("连接失败");

em.Join("192.168.1.100");
```

### 5. 发送消息

```csharp
// 发送给指定对端
em.SendMessage(peerId, "move", Serialize(from, to));

// 广播给所有对端
em.BroadcastMessage("chat", Encoding.UTF8.GetBytes("Hello!"));

// 接收消息
em.MessageChannel.MessageReceived += (peerId, channel, data) => {
    if (channel == "move") HandleMove(data);
};
```

### 6. 使用房间系统

**Host 端：**

```csharp
var em = GetNode<EasyMultiplayer>("/root/EasyMultiplayer");

em.RoomHost.GuestJoined += (peerId) => GD.Print($"客人 {peerId} 加入");
em.RoomHost.AllReady += () => em.RoomHost.StartGame();

em.CreateRoom("我的房间", "chess");
```

**Client 端：**

```csharp
var em = GetNode<EasyMultiplayer>("/root/EasyMultiplayer");

em.RoomClient.StartSearching();

// 监听发现的房间
em.Discovery.RoomFound += (room) => {
    GD.Print($"发现房间: {room.Info.HostName} @ {room.HostIp}");
    em.JoinRoom(room.HostIp);
};
```

### 7. 主动退出

```csharp
// 退出前先通知对端，让对端区分主动退出与意外断线
em.GracefulDisconnect("quit");

// 对端监听
em.PeerGracefulQuit += (peerId, reason) => {
    GD.Print($"对端 {peerId} 主动退出，原因: {reason}");
};
```

---

## 架构概览

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

**模块职责：**

| 模块 | 职责 |
|------|------|
| `EasyMultiplayer` | Autoload 单例，统一 API 入口，管理连接状态机 |
| `ENetTransport` | ENet 传输层实现，不依赖 MultiplayerAPI |
| `UdpBroadcastDiscovery` | UDP 广播房间发现 |
| `HeartbeatManager` | Ping/Pong 心跳、RTT、网络质量、断线重连 |
| `MessageChannel` | 统一消息收发，string channel 路由，频率限制 |
| `RoomHost` | 房间主机：创建房间、广播、管理准备状态 |
| `RoomClient` | 房间客户端：搜索房间、加入、准备状态 |

---

## 连接状态机

```
Disconnected ──Host()──► Hosting ──PeerJoined──► Connected
Disconnected ──Join()──► Joining ──ConnSucceeded──► Connected
Connected ──心跳超时──► Reconnecting ──重连成功──► Connected
Reconnecting ──超时/失败──► Disconnected
Any ──Disconnect()──► Disconnected
```

---

## 目录结构

```
addons/EasyMultiplayer/
├── plugin.cfg
├── EasyMultiplayerPlugin.cs
├── Core/
│   ├── EasyMultiplayer.cs        # Autoload 单例
│   ├── ConnectionState.cs        # 连接状态枚举
│   ├── EasyMultiplayerConfig.cs  # 配置 Resource
│   └── MessageChannel.cs         # 消息通道
├── Transport/
│   ├── ITransport.cs             # 传输层接口
│   └── ENetTransport.cs          # ENet 实现
├── Discovery/
│   ├── IDiscovery.cs             # 发现层接口
│   └── UdpBroadcastDiscovery.cs  # UDP 广播实现
├── Room/
│   ├── RoomHost.cs               # 房间主机
│   ├── RoomClient.cs             # 房间客户端
│   └── RoomState.cs              # 状态枚举
├── Heartbeat/
│   └── HeartbeatManager.cs       # 心跳管理器
└── docs/
    ├── PITFALLS.md               # 踩坑记录
    ├── design.md                 # 设计文档
    └── API.md                    # API 参考
```

---

## 文档

- [设计文档](docs/design.md) — 架构设计、模块详解、设计决策
- [API 参考](docs/API.md) — 完整 API 文档
- [踩坑记录](addons/EasyMultiplayer/docs/PITFALLS.md) — ENet 使用中的常见陷阱

---

## 版本

当前版本：**v1.1.0**

要求：Godot 4.x + .NET（C#）
