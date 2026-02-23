# EasyMultiplayer 踩坑记录

*记录于 2026-02-23，千棋世界联机大厅开发过程中*

---

## 1. 绝对不能设置 MultiplayerPeer 到 MultiplayerAPI

**现象：** `process_rpc: Invalid packet received`、`poll: len != 2`、`get_cached_object: ID 0 not found`

**原因：** 设置 `Multiplayer.MultiplayerPeer = _peer` 后，Godot 的 `SceneMultiplayer` 会在内部 poll 中消费数据包并尝试解析为 RPC。我们的自定义数据包格式不是 RPC，解析失败报错。同时数据包被 SceneMultiplayer 抢先消费，我们的 `Poll()` 收不到。

**解决方案：** 不设置 MultiplayerPeer 到 MultiplayerAPI。自己管理连接检测和数据包收发。

**注意：** `ProcessPriority = int.MinValue` 无法解决此问题，因为 SceneMultiplayer 的 poll 不走 Node 的 `_Process`。

---

## 2. _peer.PeerConnected 信号不可靠

**现象：** 连接 `_peer.PeerConnected += handler` 后，信号有时触发有时不触发，或者 `PeerDisconnected` 误触发导致 `_connectedPeers` 被清空。

**原因：** `ENetMultiplayerPeer` 的信号在 C++ 层的 `poll()` 中通过 `emit_signal` 触发。不设置 MultiplayerPeer 到 MultiplayerAPI 时，信号行为不稳定。

**解决方案：** 不依赖 `_peer` 的 Godot 信号。改用以下方式检测连接：
- **客户端连接检测：** 在 `Poll()` 中轮询 `_peer.GetConnectionStatus()` 的变化（Connecting → Connected）
- **服务器端 peer 发现：** 客户端连接成功后发送握手包，服务器在数据包循环中发现新 senderId 时触发 PeerConnected
- **断开检测：** 通过心跳超时机制（HeartbeatManager）

---

## 3. GetPacketPeer 必须在 GetPacket 之前调用

**现象：** `Condition "incoming_packets.is_empty()" is true` C++ 错误

**原因：** `GetPacket()` 从 `incoming_packets` 队列取出并移除当前包。之后调用 `GetPacketPeer()` 时队列已空。

**正确顺序：**
```csharp
var senderId = (int)_peer.GetPacketPeer();  // 先读 peer ID
var rawPacket = _peer.GetPacket();           // 再取包
```

---

## 4. GetPeer(id) 对不存在的 peer ID 会打 C++ 错误

**现象：** `Condition "!peers.has(p_id)" is true. Returning: nullptr`

**原因：** `ENetMultiplayerPeer.GetPeer(peerId)` 在 C++ 层检查 peers 表，不存在时打印错误并返回 null。C# 的 try-catch 无法捕获 C++ 层的条件错误。

**解决方案：** 不调用 `GetPeer()`。通过数据包的 senderId 来发现 peer。

---

## 5. 客户端 _connectedPeers 必须手动添加 server peer

**现象：** 客户端连接成功后 `ConnectedPeers` 为空，所有 `SendMessage` 静默失败。

**原因：** `OnTransportConnectionSucceeded` 中没有 `_connectedPeers.Add(1)`。客户端的 server peer ID 固定为 1，需要手动添加。

**修复：**
```csharp
private void OnTransportConnectionSucceeded()
{
    // ...
    _connectedPeers.Add(1); // Server peer ID = 1
    _heartbeat.TrackPeer(1);
    _heartbeat.Start();
    // ...
}
```

---

## 6. SendPacket 不应检查 _knownPeers

**现象：** 服务器端发包给客户端时被 `_knownPeers.Contains(peerId)` 拦截，消息丢失。

**原因：** `_knownPeers` 通过握手包/数据包更新，可能滞后于 ENet C++ 层的 `peers` 表。C++ 层已经知道 peer 存在（`poll()` 处理了 CONNECT 事件），但我们的 `_knownPeers` 还没更新。

**解决方案：** `SendPacket` 中不检查 `_knownPeers`，直接调用 `PutPacket`，检查返回值处理错误。

---

## 7. UDP 广播自身过滤不能用 IP

**现象：** 同一台机器上两个实例互相搜不到房间。

**原因：** 用 `NetworkInterface.GetAllNetworkInterfaces()` 获取所有本机 IP 来过滤自身广播。同一台机器上两个实例的 IP 相同，互相过滤掉了。

**解决方案：** 在 `RoomInfo` 中加 `InstanceId`（每个进程启动时生成随机 ID），用 InstanceId 过滤自身广播而不是 IP。

---

## 8. Dns.GetHostName + GetHostAddresses 在某些环境下失败

**现象：** `nodename nor servname provided, or not known` 错误

**原因：** macOS 上 `Dns.GetHostName()` 返回的主机名可能无法被 DNS 解析。

**解决方案：** 改用 `NetworkInterface.GetAllNetworkInterfaces()` 遍历网络接口获取 IP。

---

## 9. ENetMultiplayerPeer.PutPacket 的 peers 表

**现象：** `Invalid target peer: X` C++ 错误

**原因：** `PutPacket` 在 C++ 层检查 `peers.has(target_peer)`。peers 表在以下时机维护：
- `create_client` 时 `peers[1] = peer`
- `poll()` 处理 CONNECT 事件时 `peers[id] = event.peer`
- `_disconnect_inactive_peers()` 移除不活跃的 peer

如果在 peer 断开后仍然发包，会报此错误。

**解决方案：** 发包前检查 transport 状态（`Status == Connected`），心跳等定期发送的模块也要检查连接状态。

---

## 总结：ENetTransport 正确架构

```
不设置 MultiplayerPeer 到 MultiplayerAPI
├── 客户端连接检测：Poll() 中轮询 GetConnectionStatus() 变化
├── 客户端连接成功后：
│   ├── _knownPeers.Add(1)
│   ├── 发送握手包 SendReliable(1, HandshakeChannel, {0x01})
│   └── 触发 ConnectionSucceeded
├── 服务器 peer 发现：数据包循环中检测新 senderId
│   ├── 握手包 → PeerConnected（不传递给上层）
│   └── 普通数据包 → PeerConnected + DataReceived
├── 数据包读取：先 GetPacketPeer() 再 GetPacket()
├── 发包：直接 PutPacket，检查返回值
└── 断开检测：心跳超时（HeartbeatManager）
```
