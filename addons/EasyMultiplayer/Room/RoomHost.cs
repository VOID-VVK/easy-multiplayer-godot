using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using EasyMultiplayer.Core;
using EasyMultiplayer.Discovery;
using EasyMultiplayer.Transport;

namespace EasyMultiplayer.Room;

/// <summary>
/// 房间主机逻辑。负责创建房间、广播、等待客人加入、管理准备状态和开始游戏。
/// </summary>
/// <remarks>
/// <para>
/// 支持多人（不限于 1 个 Guest），通过 <see cref="_guests"/> 字典跟踪所有客人的准备状态。
/// 房间状态机：Idle → Waiting → Ready → Playing → Closed。
/// </para>
/// <para>
/// 此实现不依赖 EventBus，所有事件通过 Godot Signal 暴露。
/// 准备状态通过 MessageChannel 传输，而非 Godot RPC，
/// 保持与 ITransport 抽象的一致性。
/// </para>
/// </remarks>
public partial class RoomHost : Node
{
    // ── 内部消息通道标识 ──

    /// <summary>房间内部控制消息通道。</summary>
    private const string RoomChannel = "__room_ctrl";

    // ── 消息类型前缀 ──

    private const string MsgGuestReady = "guest_ready:";
    private const string MsgHostReady = "host_ready:";
    private const string MsgGameStart = "game_start:";
    private const string MsgRoomInfo = "room_info:";

    // ── 依赖 ──

    private ITransport? _transport;
    private IDiscovery? _discovery;
    private MessageChannel? _messageChannel;
    private EasyMultiplayerConfig _config = new();

    // ── 状态 ──

    private RoomState _state = RoomState.Idle;

    /// <summary>当前房间状态。</summary>
    public RoomState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            var old = _state;
            _state = value;
            GD.Print($"[RoomHost] 房间状态: {old} → {value}");
            EmitSignal(SignalName.RoomStateChanged, (int)old, (int)value);
        }
    }

    /// <summary>房间名称。</summary>
    public string RoomName { get; private set; } = "";

    /// <summary>游戏类型标识。</summary>
    public string GameType { get; private set; } = "";

    /// <summary>房间端口。</summary>
    public int Port { get; private set; }

    /// <summary>最大玩家数（含 Host）。</summary>
    public int MaxPlayers { get; private set; } = 2;

    /// <summary>房主准备状态。</summary>
    public bool HostReady { get; private set; }

    /// <summary>保存的游戏版本号，用于广播。</summary>
    private string _gameVersion = "1.0.0";

    /// <summary>所有客人的准备状态。键为 peerId。</summary>
    private readonly Dictionary<int, bool> _guests = new();

    /// <summary>获取当前客人 peer ID 列表。</summary>
    public IReadOnlyCollection<int> GuestPeerIds => _guests.Keys;

    /// <summary>当前玩家数（含 Host）。</summary>
    public int PlayerCount => 1 + _guests.Count;

    // ── Godot 信号 ──

    /// <summary>房间状态转换时触发。</summary>
    [Signal]
    public delegate void RoomStateChangedEventHandler(int oldState, int newState);

    /// <summary>客人加入房间时触发。</summary>
    [Signal]
    public delegate void GuestJoinedEventHandler(int peerId);

    /// <summary>客人离开房间时触发。</summary>
    [Signal]
    public delegate void GuestLeftEventHandler(int peerId);

    /// <summary>客人准备状态变更时触发。</summary>
    [Signal]
    public delegate void GuestReadyChangedEventHandler(int peerId, bool ready);

    /// <summary>所有人（Host + 全部 Guest）都已准备时触发。</summary>
    [Signal]
    public delegate void AllReadyEventHandler();

    /// <summary>游戏即将开始时触发。</summary>
    [Signal]
    public delegate void GameStartingEventHandler(string gameType);

    // ── 初始化 ──

    /// <summary>
    /// 设置依赖。应在使用前调用。
    /// </summary>
    /// <param name="transport">传输层实例。</param>
    /// <param name="discovery">发现层实例。</param>
    /// <param name="messageChannel">消息通道实例。</param>
    /// <param name="config">配置资源。</param>
    /// <param name="gameVersion">游戏版本号，用于广播。</param>
    public void Setup(ITransport transport, IDiscovery discovery, MessageChannel messageChannel, EasyMultiplayerConfig config, string gameVersion = "1.0.0")
    {
        // 清理旧绑定
        if (_transport != null)
        {
            _transport.PeerConnected -= OnPeerConnected;
            _transport.PeerDisconnected -= OnPeerDisconnected;
        }
        if (_messageChannel != null)
        {
            _messageChannel.MessageReceived -= OnMessageReceived;
        }

        _transport = transport;
        _discovery = discovery;
        _messageChannel = messageChannel;
        _config = config;
        _gameVersion = gameVersion;

        _transport.PeerConnected += OnPeerConnected;
        _transport.PeerDisconnected += OnPeerDisconnected;
        _messageChannel.MessageReceived += OnMessageReceived;
    }

    // ── 公共 API ──

    /// <summary>
    /// 创建房间并开始广播。
    /// </summary>
    /// <param name="name">房间名称。</param>
    /// <param name="gameType">游戏类型标识。</param>
    /// <param name="port">监听端口，默认从配置读取。</param>
    /// <param name="maxPlayers">最大玩家数（含 Host），默认从配置读取。</param>
    /// <returns>操作结果。</returns>
    public Error CreateRoom(string name, string gameType, int port = -1, int maxPlayers = -1)
    {
        if (_transport == null || _discovery == null)
        {
            GD.PrintErr("[RoomHost] 未初始化，请先调用 Setup()");
            return Error.Unconfigured;
        }

        if (State != RoomState.Idle && State != RoomState.Closed)
        {
            GD.PrintErr("[RoomHost] 无法创建房间：当前状态不允许");
            return Error.AlreadyInUse;
        }

        RoomName = name;
        GameType = gameType;
        Port = port > 0 ? port : _config.Port;
        MaxPlayers = maxPlayers > 0 ? maxPlayers : _config.MaxClients + 1;
        HostReady = false;
        _guests.Clear();

        // 创建传输层主机
        var error = _transport.CreateHost(Port, MaxPlayers - 1);
        if (error != Error.Ok)
        {
            GD.PrintErr($"[RoomHost] 创建主机失败: {error}");
            return error;
        }

        // 开始广播
        _discovery.StartBroadcast(new RoomInfo
        {
            HostName = name,
            GameType = gameType,
            Port = Port,
            PlayerCount = 1,
            MaxPlayers = MaxPlayers,
            Version = _gameVersion
        });

        State = RoomState.Waiting;
        GD.Print($"[RoomHost] 房间已创建: {name} ({gameType}) 端口:{Port} 最大玩家:{MaxPlayers}");
        return Error.Ok;
    }

    /// <summary>
    /// 关闭房间，清理所有资源。
    /// </summary>
    public void CloseRoom()
    {
        _discovery?.StopBroadcast();

        // 断开所有客人
        if (_transport != null)
        {
            foreach (var peerId in _guests.Keys)
            {
                _transport.DisconnectPeer(peerId);
            }
        }

        HostReady = false;
        _guests.Clear();
        State = RoomState.Closed;
        GD.Print("[RoomHost] 房间已关闭");
    }

    /// <summary>
    /// 设置房主准备状态，并通知所有客人。
    /// </summary>
    /// <param name="ready">是否准备就绪。</param>
    public void SetHostReady(bool ready)
    {
        if (State != RoomState.Ready)
        {
            GD.Print("[RoomHost] 无法设置准备状态：房间不在 Ready 状态");
            return;
        }

        HostReady = ready;
        GD.Print($"[RoomHost] 房主准备状态: {ready}");

        // 通知所有客人
        _messageChannel?.Broadcast(RoomChannel,
            Encoding.UTF8.GetBytes($"{MsgHostReady}{ready}"));

        CheckAllReady();
    }

    /// <summary>
    /// 开始游戏。仅在所有人都准备就绪时有效。
    /// </summary>
    public void StartGame()
    {
        if (State != RoomState.Ready)
        {
            GD.PrintErr("[RoomHost] 无法开始游戏：房间不在 Ready 状态");
            return;
        }

        if (!HostReady || !AreAllGuestsReady())
        {
            GD.PrintErr("[RoomHost] 无法开始游戏：未全部准备就绪");
            return;
        }

        State = RoomState.Playing;

        // 通知所有客人游戏开始
        _messageChannel?.Broadcast(RoomChannel,
            Encoding.UTF8.GetBytes($"{MsgGameStart}{GameType}"));

        GD.Print($"[RoomHost] 游戏开始: {GameType}");
        EmitSignal(SignalName.GameStarting, GameType);
    }

    /// <summary>
    /// 重置所有准备状态。
    /// </summary>
    public void ResetReadyState()
    {
        HostReady = false;
        var peerIds = new List<int>(_guests.Keys);
        foreach (var peerId in peerIds)
        {
            _guests[peerId] = false;
        }
        GD.Print("[RoomHost] 准备状态已重置");
    }

    /// <summary>
    /// 检查指定客人是否已准备。
    /// </summary>
    /// <param name="peerId">客人 peer ID。</param>
    /// <returns>是否已准备，未找到返回 false。</returns>
    public bool IsGuestReady(int peerId)
    {
        return _guests.TryGetValue(peerId, out var ready) && ready;
    }

    /// <summary>
    /// 检查是否所有客人都已准备。
    /// </summary>
    /// <returns>所有客人都已准备返回 true。</returns>
    public bool AreAllGuestsReady()
    {
        if (_guests.Count == 0) return false;
        foreach (var ready in _guests.Values)
        {
            if (!ready) return false;
        }
        return true;
    }

    // ── Node 生命周期 ──

    /// <summary>
    /// 节点退出场景树时清理。
    /// </summary>
    public override void _ExitTree()
    {
        if (State != RoomState.Idle && State != RoomState.Closed)
        {
            CloseRoom();
        }

        if (_transport != null)
        {
            _transport.PeerConnected -= OnPeerConnected;
            _transport.PeerDisconnected -= OnPeerDisconnected;
        }
        if (_messageChannel != null)
        {
            _messageChannel.MessageReceived -= OnMessageReceived;
        }
    }

    // ── 内部事件处理 ──

    /// <summary>
    /// 对端连接事件处理。
    /// </summary>
    private void OnPeerConnected(int peerId)
    {
        if (State != RoomState.Waiting && State != RoomState.Ready) return;

        // 检查是否已满
        if (PlayerCount >= MaxPlayers)
        {
            GD.Print($"[RoomHost] 房间已满，拒绝 peer {peerId}");
            _transport?.DisconnectPeer(peerId);
            return;
        }

        _guests[peerId] = false;
        GD.Print($"[RoomHost] 客人已加入: {peerId} (当前 {PlayerCount}/{MaxPlayers})");
        EmitSignal(SignalName.GuestJoined, peerId);

        // 发送房间信息给新加入的客人
        var roomInfoMsg = $"{MsgRoomInfo}{RoomName}|{GameType}";
        _messageChannel?.SendReliable(peerId, RoomChannel,
            Encoding.UTF8.GetBytes(roomInfoMsg));

        // 更新广播中的玩家数
        UpdateBroadcast();

        // 如果房间已满，停止广播并进入 Ready 状态
        if (PlayerCount >= MaxPlayers)
        {
            _discovery?.StopBroadcast();
            if (State == RoomState.Waiting)
            {
                State = RoomState.Ready;
            }
        }
        else if (State == RoomState.Waiting && _guests.Count > 0)
        {
            // 有人加入但未满，也进入 Ready 状态（允许提前开始）
            State = RoomState.Ready;
        }
    }

    /// <summary>
    /// 对端断开事件处理。
    /// </summary>
    private void OnPeerDisconnected(int peerId)
    {
        if (!_guests.ContainsKey(peerId)) return;

        _guests.Remove(peerId);
        GD.Print($"[RoomHost] 客人已离开: {peerId} (剩余 {PlayerCount}/{MaxPlayers})");
        EmitSignal(SignalName.GuestLeft, peerId);

        // 客人离开时重置 HostReady 状态，确保多人场景下准备状态一致
        HostReady = false;

        // 如果在 Ready 或 Waiting 状态，根据剩余人数调整
        if (State == RoomState.Ready)
        {
            if (_guests.Count == 0)
            {
                // 没有客人了，回到 Waiting 并重新广播
                _discovery?.StartBroadcast(new RoomInfo
                {
                    HostName = RoomName,
                    GameType = GameType,
                    Port = Port,
                    PlayerCount = 1,
                    MaxPlayers = MaxPlayers,
                    Version = _gameVersion
                });
                State = RoomState.Waiting;
            }
            else
            {
                // 还有客人，重新广播（如果未满）
                if (PlayerCount < MaxPlayers)
                {
                    UpdateBroadcast();
                    _discovery?.StartBroadcast(new RoomInfo
                    {
                        HostName = RoomName,
                        GameType = GameType,
                        Port = Port,
                        PlayerCount = PlayerCount,
                        MaxPlayers = MaxPlayers,
                        Version = _gameVersion
                    });
                }
            }
        }
    }

    /// <summary>
    /// 消息接收处理。处理房间控制消息。
    /// </summary>
    private void OnMessageReceived(int peerId, string channel, byte[] data)
    {
        if (channel != RoomChannel) return;

        var msg = Encoding.UTF8.GetString(data);

        if (msg.StartsWith(MsgGuestReady))
        {
            var readyStr = msg.Substring(MsgGuestReady.Length);
            if (bool.TryParse(readyStr, out var ready) && _guests.ContainsKey(peerId))
            {
                _guests[peerId] = ready;
                GD.Print($"[RoomHost] 客人 {peerId} 准备状态: {ready}");
                EmitSignal(SignalName.GuestReadyChanged, peerId, ready);
                CheckAllReady();
            }
        }
    }

    /// <summary>
    /// 检查是否所有人都已准备，如果是则触发 AllReady 信号。
    /// </summary>
    private void CheckAllReady()
    {
        if (!HostReady || !AreAllGuestsReady()) return;

        GD.Print("[RoomHost] 所有人已就绪！");
        EmitSignal(SignalName.AllReady);
    }

    /// <summary>
    /// 更新广播中的玩家数信息。
    /// </summary>
    private void UpdateBroadcast()
    {
        if (_discovery == null || !_discovery.IsBroadcasting) return;

        // 停止旧广播，启动新广播（更新玩家数）
        _discovery.StopBroadcast();
        _discovery.StartBroadcast(new RoomInfo
        {
            HostName = RoomName,
            GameType = GameType,
            Port = Port,
            PlayerCount = PlayerCount,
            MaxPlayers = MaxPlayers,
            Version = _gameVersion
        });
    }
}
