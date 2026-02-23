using Godot;
using System;
using System.Collections.Generic;

namespace EasyMultiplayer.Transport;

/// <summary>
/// 基于 Godot ENetMultiplayerPeer 的默认传输层实现。
/// </summary>
/// <remarks>
/// <para>
/// 封装 Godot 的 <see cref="ENetMultiplayerPeer"/>，不设置 MultiplayerPeer 到 MultiplayerAPI，
/// 通过握手包和数据包 senderId 驱动 peer 发现。
/// </para>
/// <para>
/// 使用者需在每帧调用 <see cref="Poll"/> 以驱动事件循环（通常由 EasyMultiplayer 单例的 _Process 调用）。
/// </para>
/// </remarks>
public class ENetTransport : ITransport
{
    private ENetMultiplayerPeer? _peer;

    /// <summary>记住上次连接的地址，用于重连。</summary>
    private string _lastAddress = "";

    /// <summary>记住上次连接的端口，用于重连。</summary>
    private int _lastPort;

    private readonly HashSet<int> _knownPeers = new();

    /// <summary>内部握手通道标识，服务器收到后触发 PeerConnected，不传递给上层。</summary>
    private const int HandshakeChannel = int.MinValue;

    /// <summary>客户端上一帧的 ENet 连接状态，用于检测状态转换。</summary>
    private MultiplayerPeer.ConnectionStatus _prevClientStatus = MultiplayerPeer.ConnectionStatus.Disconnected;

    // ── ITransport 事件 ──

    /// <inheritdoc />
    public event Action<int>? PeerConnected;

    /// <inheritdoc />
    public event Action<int>? PeerDisconnected;

    /// <inheritdoc />
    public event Action<int, int, byte[]>? DataReceived;

    /// <inheritdoc />
    public event Action? ConnectionSucceeded;

    /// <inheritdoc />
    public event Action? ConnectionFailed;

    // ── ITransport 属性 ──

    /// <inheritdoc />
    public bool IsServer { get; private set; }

    /// <inheritdoc />
    public int UniqueId { get; private set; }

    /// <inheritdoc />
    public TransportStatus Status { get; private set; } = TransportStatus.Disconnected;

    /// <summary>
    /// 保留接口兼容性，内部不使用 SceneTree（不设置 MultiplayerPeer 到 MultiplayerAPI）。
    /// </summary>
    public void Initialize(SceneTree sceneTree)
    {
        // 不设置 MultiplayerPeer 到 MultiplayerAPI，使用信号 + 握手包发现机制
    }

    /// <summary>
    /// 清理资源。
    /// </summary>
    public void Cleanup()
    {
        _knownPeers.Clear();
    }

    // ── ITransport 生命周期 ──

    /// <inheritdoc />
    public Error CreateHost(int port, int maxClients)
    {
        if (Status != TransportStatus.Disconnected)
        {
            GD.PrintErr("[ENetTransport] 无法创建主机：当前状态不是 Disconnected");
            return Error.AlreadyInUse;
        }

        _peer = new ENetMultiplayerPeer();
        var error = _peer.CreateServer(port, maxClients);
        if (error != Error.Ok)
        {
            GD.PrintErr($"[ENetTransport] 创建主机失败: {error}");
            _peer = null;
            return error;
        }

        _lastPort = port;
        IsServer = true;
        UniqueId = 1;
        Status = TransportStatus.Connected;
        GD.Print($"[ENetTransport] 主机已创建，端口: {port}, 最大客户端: {maxClients}");
        return Error.Ok;
    }

    /// <inheritdoc />
    public Error CreateClient(string address, int port)
    {
        if (Status != TransportStatus.Disconnected)
        {
            GD.PrintErr("[ENetTransport] 无法连接：当前状态不是 Disconnected");
            return Error.AlreadyInUse;
        }

        _peer = new ENetMultiplayerPeer();
        var error = _peer.CreateClient(address, port);
        if (error != Error.Ok)
        {
            GD.PrintErr($"[ENetTransport] 连接失败: {error}");
            _peer = null;
            return error;
        }

        _lastAddress = address;
        _lastPort = port;
        IsServer = false;
        Status = TransportStatus.Connecting;
        _prevClientStatus = MultiplayerPeer.ConnectionStatus.Connecting;
        GD.Print($"[ENetTransport] 正在连接 {address}:{port}");
        return Error.Ok;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        if (_peer == null) return;

        _peer.Close();
        _peer = null;
        _knownPeers.Clear();
        IsServer = false;
        UniqueId = 0;
        Status = TransportStatus.Disconnected;
        _prevClientStatus = MultiplayerPeer.ConnectionStatus.Disconnected;
        GD.Print("[ENetTransport] 已断开连接");
    }

    /// <inheritdoc />
    public void DisconnectPeer(int peerId)
    {
        if (_peer == null) return;
        _peer.DisconnectPeer(peerId);
        GD.Print($"[ENetTransport] 已断开对端: {peerId}");
    }

    // ── ENet 信号处理 ──

    private void OnENetPeerConnected(long id)
    {
        var peerId = (int)id;
        _knownPeers.Add(peerId);
        GD.Print($"[ENetTransport] 对端已连接 (信号): {peerId}");
        // 服务器端：立即通知上层（客户端连接由 ConnectionSucceeded 通知）
        if (IsServer)
            PeerConnected?.Invoke(peerId);
    }

    private void OnENetPeerDisconnected(long id)
    {
        var peerId = (int)id;
        _knownPeers.Remove(peerId);
        GD.Print($"[ENetTransport] 对端已断开 (信号): {peerId}");
        if (IsServer)
            PeerDisconnected?.Invoke(peerId);
    }

    /// <inheritdoc />
    public void Poll()
    {
        if (_peer == null || Status == TransportStatus.Disconnected) return;

        _peer.Poll();

        // 客户端：通过轮询 ENet 连接状态检测连接成功/失败
        if (!IsServer)
        {
            var currentStatus = _peer.GetConnectionStatus();

            if (_prevClientStatus != currentStatus)
            {
                if (_prevClientStatus == MultiplayerPeer.ConnectionStatus.Connecting &&
                    currentStatus == MultiplayerPeer.ConnectionStatus.Connected)
                {
                    _prevClientStatus = currentStatus;
                    UniqueId = _peer.GetUniqueId();
                    Status = TransportStatus.Connected;
                    _knownPeers.Add(1); // 服务器 peer ID = 1（信号可能已添加，HashSet 幂等）
                    GD.Print($"[ENetTransport] 已连接到服务器, UniqueId={UniqueId}");
                    // 发送握手包，让服务器发现此客户端
                    SendReliable(1, HandshakeChannel, new byte[] { 0x01 });
                    ConnectionSucceeded?.Invoke();
                }
                else if (currentStatus == MultiplayerPeer.ConnectionStatus.Disconnected)
                {
                    _prevClientStatus = currentStatus;
                    if (Status == TransportStatus.Connecting)
                    {
                        GD.PrintErr("[ENetTransport] 连接服务器失败");
                        _peer = null;
                        Status = TransportStatus.Disconnected;
                        ConnectionFailed?.Invoke();
                        return;
                    }
                    else
                    {
                        GD.Print("[ENetTransport] 与服务器的连接已断开");
                        Status = TransportStatus.Disconnected;
                        PeerDisconnected?.Invoke(1); // 服务器 peer ID = 1
                        return;
                    }
                }
                else
                {
                    _prevClientStatus = currentStatus;
                }
            }
        }

        // 读取并消费所有数据包，防止 Godot RPC 系统处理自定义包
        while (_peer != null && _peer.GetAvailablePacketCount() > 0)
        {
            var senderId = (int)_peer.GetPacketPeer();
            var rawPacket = _peer.GetPacket();

            if (rawPacket == null || rawPacket.Length < 4) continue;

            var (channel, data) = ParsePacket(rawPacket);

            if (channel == HandshakeChannel)
            {
                // 握手包兜底：若信号未触发，服务器通过此包发现新客户端
                if (IsServer && !_knownPeers.Contains(senderId))
                {
                    _knownPeers.Add(senderId);
                    GD.Print($"[ENetTransport] 对端已连接 (握手兜底): {senderId}");
                    PeerConnected?.Invoke(senderId);
                }
                // 握手包不传递给上层
            }
            else
            {
                // 普通数据包：若服务器收到未知 peer 的包，也触发 PeerConnected（兜底）
                if (IsServer && !_knownPeers.Contains(senderId))
                {
                    _knownPeers.Add(senderId);
                    GD.Print($"[ENetTransport] 对端已连接 (首包兜底): {senderId}");
                    PeerConnected?.Invoke(senderId);
                }
                DataReceived?.Invoke(senderId, channel, data);
            }
        }

        // 服务器端：检测整体连接断开（安全兜底）
        if (IsServer && _peer != null)
        {
            var serverStatus = _peer.GetConnectionStatus();
            if (serverStatus == MultiplayerPeer.ConnectionStatus.Disconnected)
            {
                GD.Print("[ENetTransport] 服务器连接已断开，通知所有已知对端");
                var snapshot = new List<int>(_knownPeers);
                _knownPeers.Clear();
                Status = TransportStatus.Disconnected;
                foreach (var pid in snapshot)
                    PeerDisconnected?.Invoke(pid);
            }
        }
    }

    // ── ITransport 数据发送 ──

    /// <inheritdoc />
    public void SendReliable(int peerId, int channel, byte[] data)
    {
        if (_peer == null || Status != TransportStatus.Connected) return;
        _peer.TransferMode = MultiplayerPeer.TransferModeEnum.Reliable;
        var packet = BuildPacket(channel, data);
        SendPacket(peerId, packet);
    }

    /// <inheritdoc />
    public void SendUnreliable(int peerId, int channel, byte[] data)
    {
        if (_peer == null || Status != TransportStatus.Connected) return;
        _peer.TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable;
        var packet = BuildPacket(channel, data);
        SendPacket(peerId, packet);
    }

    private void SendPacket(int peerId, byte[] packet)
    {
        if (_peer == null) return;
        _peer.SetTargetPeer(peerId);
        var err = _peer.PutPacket(packet);
        if (err != Error.Ok)
            GD.PrintErr($"[ENetTransport] PutPacket 失败 (peer={peerId}): {err}");
    }

    // ── 辅助方法 ──

    private static byte[] BuildPacket(int channel, byte[] data)
    {
        var packet = new byte[4 + data.Length];
        BitConverter.GetBytes(channel).CopyTo(packet, 0);
        data.CopyTo(packet, 4);
        return packet;
    }

    private static (int channel, byte[] data) ParsePacket(byte[] packet)
    {
        if (packet.Length < 4)
            return (0, packet);
        var channel = BitConverter.ToInt32(packet, 0);
        var data = new byte[packet.Length - 4];
        Array.Copy(packet, 4, data, 0, data.Length);
        return (channel, data);
    }

    /// <summary>获取上次连接的地址（用于重连）。</summary>
    public string LastAddress => _lastAddress;

    /// <summary>获取上次连接的端口（用于重连）。</summary>
    public int LastPort => _lastPort;
}
