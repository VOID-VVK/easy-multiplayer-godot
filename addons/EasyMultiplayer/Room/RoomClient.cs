using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using EasyMultiplayer.Core;
using EasyMultiplayer.Discovery;
using EasyMultiplayer.Transport;

namespace EasyMultiplayer.Room;

/// <summary>
/// 房间客户端逻辑。负责搜索房间、加入房间、管理准备状态。
/// </summary>
/// <remarks>
/// <para>
/// 客户端状态机：Idle → Searching → Joining → InRoom → GameStarting。
/// 通过 IDiscovery 搜索局域网房间，通过 ITransport 连接到主机。
/// </para>
/// <para>
/// 此实现不依赖 EventBus 和 Godot RPC，
/// 所有房间控制消息通过 MessageChannel 传输，保持与 ITransport 抽象的一致性。
/// </para>
/// </remarks>
public partial class RoomClient : Node
{
    // ── 内部消息通道标识（与 RoomHost 一致） ──

    /// <summary>房间内部控制消息通道。</summary>
    private const string RoomChannel = "__room_ctrl";

    // ── 消息类型前缀（与 RoomHost 一致） ──

    private const string MsgGuestReady = "guest_ready:";
    private const string MsgHostReady = "host_ready:";
    private const string MsgGameStart = "game_start:";
    private const string MsgRoomInfo = "room_info:";

    // ── 依赖 ──

    private ITransport? _transport;
    private IDiscovery? _discovery;
    private MessageChannel? _messageChannel;

    // ── 状态 ──

    private ClientState _state = ClientState.Idle;

    /// <summary>当前客户端状态。</summary>
    public ClientState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            var old = _state;
            _state = value;
            GD.Print($"[RoomClient] 客户端状态: {old} → {value}");
            EmitSignal(SignalName.ClientStateChanged, (int)old, (int)value);
        }
    }

    /// <summary>当前加入的房间信息。</summary>
    public RoomInfo? CurrentRoom { get; private set; }

    /// <summary>当前房间的 Host IP。</summary>
    public string CurrentHostIp { get; private set; } = "";

    /// <summary>本地准备状态。</summary>
    public bool IsReady { get; private set; }

    /// <summary>房主准备状态。</summary>
    public bool HostReady { get; private set; }

    // ── Godot 信号 ──

    /// <summary>客户端状态转换时触发。</summary>
    [Signal]
    public delegate void ClientStateChangedEventHandler(int oldState, int newState);

    /// <summary>成功加入房间时触发。</summary>
    [Signal]
    public delegate void JoinSucceededEventHandler(string roomName, string gameType);

    /// <summary>加入房间失败时触发。</summary>
    [Signal]
    public delegate void JoinFailedEventHandler(string reason);

    /// <summary>房主准备状态变更时触发。</summary>
    [Signal]
    public delegate void HostReadyChangedEventHandler(bool ready);

    /// <summary>收到游戏开始通知时触发。</summary>
    [Signal]
    public delegate void GameStartingEventHandler(string gameType);

    /// <summary>与房间断开连接时触发。</summary>
    [Signal]
    public delegate void DisconnectedFromRoomEventHandler(string reason);

    // ── 初始化 ──

    /// <summary>
    /// 设置依赖。应在使用前调用。
    /// </summary>
    /// <param name="transport">传输层实例。</param>
    /// <param name="discovery">发现层实例。</param>
    /// <param name="messageChannel">消息通道实例。</param>
    public void Setup(ITransport transport, IDiscovery discovery, MessageChannel messageChannel)
    {
        // 清理旧绑定
        if (_transport != null)
        {
            _transport.ConnectionSucceeded -= OnConnectionSucceeded;
            _transport.ConnectionFailed -= OnConnectionFailed;
            _transport.PeerDisconnected -= OnPeerDisconnected;
        }
        if (_messageChannel != null)
        {
            _messageChannel.MessageReceived -= OnMessageReceived;
        }

        _transport = transport;
        _discovery = discovery;
        _messageChannel = messageChannel;

        _transport.ConnectionSucceeded += OnConnectionSucceeded;
        _transport.ConnectionFailed += OnConnectionFailed;
        _transport.PeerDisconnected += OnPeerDisconnected;
        _messageChannel.MessageReceived += OnMessageReceived;
    }

    // ── 公共 API ──

    /// <summary>
    /// 开始搜索局域网房间。
    /// </summary>
    public void StartSearching()
    {
        if (State != ClientState.Idle)
        {
            GD.PrintErr("[RoomClient] 无法搜索：当前状态不允许");
            return;
        }

        _discovery?.StartListening();
        State = ClientState.Searching;
        GD.Print("[RoomClient] 开始搜索房间");
    }

    /// <summary>
    /// 停止搜索。
    /// </summary>
    public void StopSearching()
    {
        _discovery?.StopListening();
        if (State == ClientState.Searching)
        {
            State = ClientState.Idle;
        }
    }

    /// <summary>
    /// 获取当前发现的房间列表。
    /// </summary>
    /// <returns>房间列表，键为 "ip:port"。</returns>
    public IReadOnlyDictionary<string, DiscoveredRoom>? GetDiscoveredRooms()
    {
        return _discovery?.Rooms;
    }

    /// <summary>
    /// 加入指定房间。
    /// </summary>
    /// <param name="hostIp">主机 IP 地址。</param>
    /// <param name="port">主机端口。</param>
    /// <returns>操作结果。</returns>
    public Error JoinRoom(string hostIp, int port)
    {
        if (_transport == null)
        {
            GD.PrintErr("[RoomClient] 未初始化，请先调用 Setup()");
            return Error.Unconfigured;
        }

        if (State != ClientState.Searching && State != ClientState.Idle)
        {
            GD.PrintErr("[RoomClient] 无法加入：当前状态不允许");
            return Error.AlreadyInUse;
        }

        // 停止搜索
        _discovery?.StopListening();

        // 查找房间信息
        var key = $"{hostIp}:{port}";
        if (_discovery?.Rooms.TryGetValue(key, out var room) == true)
        {
            CurrentRoom = room.Info;
        }
        else
        {
            CurrentRoom = new RoomInfo { Port = port };
        }

        CurrentHostIp = hostIp;
        IsReady = false;
        HostReady = false;

        var error = _transport.CreateClient(hostIp, port);
        if (error != Error.Ok)
        {
            GD.PrintErr($"[RoomClient] 加入房间失败: {error}");
            State = ClientState.Idle;
            EmitSignal(SignalName.JoinFailed, error.ToString());
            return error;
        }

        State = ClientState.Joining;
        GD.Print($"[RoomClient] 正在加入房间 {hostIp}:{port}");
        return Error.Ok;
    }

    /// <summary>
    /// 离开当前房间。
    /// </summary>
    public void LeaveRoom()
    {
        _transport?.Disconnect();
        CurrentRoom = null;
        CurrentHostIp = "";
        IsReady = false;
        HostReady = false;
        State = ClientState.Idle;
        GD.Print("[RoomClient] 已离开房间");
    }

    /// <summary>
    /// 设置准备状态并通知 Host。
    /// </summary>
    /// <param name="ready">是否准备就绪。</param>
    public void SetReady(bool ready)
    {
        if (State != ClientState.InRoom)
        {
            GD.Print("[RoomClient] 无法设置准备状态：不在房间中");
            return;
        }

        IsReady = ready;
        GD.Print($"[RoomClient] 准备状态: {ready}");

        // 通知 Host（server peer ID = 1）
        _messageChannel?.SendReliable(1, RoomChannel,
            Encoding.UTF8.GetBytes($"{MsgGuestReady}{ready}"));
    }

    // ── Node 生命周期 ──

    /// <summary>
    /// 节点退出场景树时清理。
    /// </summary>
    public override void _ExitTree()
    {
        if (State == ClientState.Searching)
        {
            StopSearching();
        }
        else if (State != ClientState.Idle)
        {
            LeaveRoom();
        }

        if (_transport != null)
        {
            _transport.ConnectionSucceeded -= OnConnectionSucceeded;
            _transport.ConnectionFailed -= OnConnectionFailed;
            _transport.PeerDisconnected -= OnPeerDisconnected;
        }
        if (_messageChannel != null)
        {
            _messageChannel.MessageReceived -= OnMessageReceived;
        }
    }

    // ── 内部事件处理 ──

    /// <summary>
    /// 连接成功回调。
    /// </summary>
    private void OnConnectionSucceeded()
    {
        if (State != ClientState.Joining) return;

        State = ClientState.InRoom;
        var roomName = CurrentRoom?.HostName ?? "未知房间";
        var gameType = CurrentRoom?.GameType ?? "";
        GD.Print($"[RoomClient] 已加入房间: {roomName}");
        EmitSignal(SignalName.JoinSucceeded, roomName, gameType);
    }

    /// <summary>
    /// 连接失败回调。
    /// </summary>
    private void OnConnectionFailed()
    {
        if (State != ClientState.Joining) return;

        CurrentRoom = null;
        CurrentHostIp = "";
        State = ClientState.Idle;
        GD.Print("[RoomClient] 加入房间失败");
        EmitSignal(SignalName.JoinFailed, "连接超时或被拒绝");
    }

    /// <summary>
    /// 对端断开回调。处理 Host 关闭房间的情况。
    /// </summary>
    private void OnPeerDisconnected(int peerId)
    {
        // Server peer ID = 1
        if (peerId != 1) return;

        if (State == ClientState.InRoom || State == ClientState.GameStarting)
        {
            CurrentRoom = null;
            CurrentHostIp = "";
            IsReady = false;
            HostReady = false;
            State = ClientState.Idle;
            GD.Print("[RoomClient] 房主已关闭房间");
            EmitSignal(SignalName.DisconnectedFromRoom, "房主已关闭房间");
        }
    }

    /// <summary>
    /// 消息接收处理。处理房间控制消息。
    /// </summary>
    private void OnMessageReceived(int peerId, string channel, byte[] data)
    {
        if (channel != RoomChannel) return;

        var msg = Encoding.UTF8.GetString(data);

        if (msg.StartsWith(MsgHostReady))
        {
            var readyStr = msg.Substring(MsgHostReady.Length);
            if (bool.TryParse(readyStr, out var ready))
            {
                HostReady = ready;
                GD.Print($"[RoomClient] 房主准备状态: {ready}");
                EmitSignal(SignalName.HostReadyChanged, ready);
            }
        }
        else if (msg.StartsWith(MsgGameStart))
        {
            var gameType = msg.Substring(MsgGameStart.Length);
            GD.Print($"[RoomClient] 游戏即将开始: {gameType}");
            State = ClientState.GameStarting;
            EmitSignal(SignalName.GameStarting, gameType);
        }
        else if (msg.StartsWith(MsgRoomInfo))
        {
            // 解析房间信息：roomName|gameType
            var info = msg.Substring(MsgRoomInfo.Length);
            var parts = info.Split('|', 2);
            if (parts.Length >= 2 && CurrentRoom != null)
            {
                CurrentRoom.HostName = parts[0];
                CurrentRoom.GameType = parts[1];
                GD.Print($"[RoomClient] 收到房间信息: {parts[0]} ({parts[1]})");
            }
        }
    }
}
