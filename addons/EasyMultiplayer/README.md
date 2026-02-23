# EasyMultiplayer

Godot 4.x C# 局域网多人游戏插件。提供房间管理、UDP 广播发现、ENet 传输、断线重连、心跳检测、优雅退出等开箱即用的功能。

## 功能

- **房间管理** — Host 创建房间并广播，Client 自动发现并加入
- **UDP 广播发现** — 局域网内自动发现房间，无需手动输入 IP
- **ENet 传输层** — 基于 Godot 内置 ENet，可靠/不可靠消息均支持
- **心跳检测** — 定期 Ping/Pong，自动检测对端存活状态
- **断线重连** — 网络抖动时自动重连，Host 端保留重连窗口
- **优雅退出** — 主动退出前发送通知，对端可区分主动退出与意外断线
- **版本校验** — 连接时自动交换版本号，版本不匹配自动踢出
- **消息通道** — 逻辑通道隔离，支持发送速率限制

## 要求

- Godot 4.x（.NET / C# 版本）
- .NET 8+

## 安装

1. 将 `addons/EasyMultiplayer/` 目录复制到你的项目 `addons/` 下
2. 在 Godot 编辑器中启用插件：**项目 → 项目设置 → 插件 → EasyMultiplayer → 启用**
3. 将 `EasyMultiplayer` 节点（`addons/EasyMultiplayer/Core/EasyMultiplayer.cs`）添加为 Autoload：
   **项目 → 项目设置 → Autoload**，路径填 `res://addons/EasyMultiplayer/Core/EasyMultiplayer.cs`，名称填 `Net`

## 快速开始

```csharp
// 获取单例
var net = GetNode<EasyMultiplayer.Core.EasyMultiplayer>("/root/Net");
net.GameVersion = "1.0.0";

// 监听信号
net.ConnectionSucceeded += () => GD.Print("连接成功");
net.PeerJoined += (id) => GD.Print($"玩家加入: {id}");
net.PeerLeft += (id) => GD.Print($"玩家离开: {id}");

// 创建房间（Host）
net.CreateRoom("我的房间", "MyGame");

// 发现并加入房间（Client）
net.Discovery.RoomFound += (room) =>
{
    GD.Print($"发现房间: {room.Info.HostName} @ {room.HostIp}:{room.Info.Port}");
    net.JoinRoom(room.HostIp, room.Info.Port);
};
net.RoomClient.StartSearching();

// 发送消息
net.SendMessage(peerId, "chat", "Hello!");
net.BroadcastMessage("game_state", data);

// 优雅退出
net.GracefulDisconnect("quit");
```

## API 概要

### EasyMultiplayer（主入口）

| 方法/属性 | 说明 |
|---|---|
| `Host(port?, maxClients?)` | 作为主机开始监听 |
| `Join(address, port?)` | 作为客户端连接主机 |
| `Disconnect()` | 断开连接 |
| `GracefulDisconnect(reason?)` | 优雅退出（先通知再断开） |
| `CreateRoom(name, gameType, port?)` | 创建房间 |
| `JoinRoom(hostIp, port?)` | 加入房间 |
| `SendMessage(peerId, channel, data)` | 发送可靠消息 |
| `BroadcastMessage(channel, data, reliable?)` | 广播消息 |
| `State` | 当前连接状态（ConnectionState 枚举） |
| `IsServer` | 是否为服务端 |
| `GameVersion` | 游戏版本号（连接时自动校验） |

### 信号

| 信号 | 说明 |
|---|---|
| `StateChanged(oldState, newState)` | 连接状态变化 |
| `PeerJoined(peerId)` | 对端连接 |
| `PeerLeft(peerId)` | 对端断开 |
| `ConnectionSucceeded()` | 客户端连接成功 |
| `ConnectionFailed()` | 客户端连接失败 |
| `VersionVerified(remoteVersion)` | 版本校验通过 |
| `VersionMismatch(local, remote)` | 版本不匹配 |
| `PeerGracefulQuit(peerId, reason)` | 对端主动退出 |
| `FullSyncRequested(peerId)` | 重连后需要全量同步 |

### EasyMultiplayerConfig（可配置参数）

| 参数 | 默认值 | 说明 |
|---|---|---|
| `Port` | 27015 | ENet 端口 |
| `MaxClients` | 1 | 最大客户端数 |
| `HeartbeatInterval` | 3.0s | 心跳间隔 |
| `DisconnectTimeout` | 10.0s | 断线超时 |
| `ReconnectTimeout` | 30.0s | Host 重连等待上限 |
| `MaxReconnectAttempts` | 20 | Client 最大重连次数 |
| `ReconnectRetryInterval` | 3.0s | 重连重试间隔 |
| `BroadcastPort` | 27016 | UDP 广播端口 |
| `BroadcastInterval` | 1.0s | 广播发送间隔 |
| `RpcMinIntervalMs` | 100ms | 消息最小发送间隔 |

## 目录结构

```
addons/EasyMultiplayer/
├── Core/
│   ├── EasyMultiplayer.cs       # 主入口 Autoload 单例
│   ├── EasyMultiplayerConfig.cs # 配置资源
│   ├── ConnectionState.cs       # 连接状态枚举
│   └── MessageChannel.cs        # 消息通道
├── Discovery/
│   ├── IDiscovery.cs            # 发现层接口
│   └── UdpBroadcastDiscovery.cs # UDP 广播实现
├── Heartbeat/
│   └── HeartbeatManager.cs      # 心跳管理器
├── Room/
│   ├── RoomHost.cs              # 房间主机
│   ├── RoomClient.cs            # 房间客户端
│   └── RoomState.cs             # 房间状态
├── Transport/
│   ├── ITransport.cs            # 传输层接口
│   └── ENetTransport.cs         # ENet 传输实现
└── EasyMultiplayerPlugin.cs     # 编辑器插件入口
```

## License

MIT License. See [LICENSE](LICENSE).
