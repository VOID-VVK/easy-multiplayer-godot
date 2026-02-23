using Godot;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EasyMultiplayer.Core;

namespace EasyMultiplayer.Discovery;

/// <summary>
/// 基于 UDP 广播的默认房间发现实现。
/// </summary>
/// <remarks>
/// <para>
/// Host 端通过 <see cref="StartBroadcast"/> 定期向局域网广播房间信息，
/// Client 端通过 <see cref="StartListening"/> 监听广播并维护可用房间列表。
/// </para>
/// <para>
/// 广播间隔、超时阈值等参数从 <see cref="EasyMultiplayerConfig"/> 读取。
/// 使用 <see cref="Node.CallDeferred"/> 将异步接收回调切回主线程，确保线程安全。
/// </para>
/// <para>
/// Magic 标识为 <c>EASYMULTI_V1</c>，用于过滤非本插件的广播包。
/// </para>
/// </remarks>
public partial class UdpBroadcastDiscovery : Node, IDiscovery
{
    /// <summary>
    /// 配置引用，广播端口、间隔、超时等参数从此读取。
    /// </summary>
    private EasyMultiplayerConfig _config = new();

    // ── 广播端（Host） ──

    private UdpClient? _broadcaster;
    private bool _broadcasting;
    private float _broadcastTimer;
    private RoomInfo? _broadcastInfo;

    // ── 监听端（Client） ──

    private UdpClient? _listener;
    private bool _listening;
    private readonly Dictionary<string, DiscoveredRoom> _discoveredRooms = new();

    // ── IDiscovery 事件 ──

    /// <inheritdoc />
    public event Action<DiscoveredRoom>? RoomFound;

    /// <inheritdoc />
    public event Action<string>? RoomLost;

    /// <inheritdoc />
    public event Action? RoomListUpdated;

    // ── IDiscovery 属性 ──

    /// <inheritdoc />
    public bool IsBroadcasting => _broadcasting;

    /// <inheritdoc />
    public bool IsListening => _listening;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DiscoveredRoom> Rooms => _discoveredRooms;

    /// <summary>
    /// 设置配置。应在使用前调用。
    /// </summary>
    /// <param name="config">EasyMultiplayer 配置资源。</param>
    public void SetConfig(EasyMultiplayerConfig config)
    {
        _config = config;
    }

    // ── 广播端 API ──

    /// <inheritdoc />
    public void StartBroadcast(RoomInfo info)
    {
        if (_broadcasting) StopBroadcast();

        _broadcastInfo = info;
        // 确保 Magic 正确
        _broadcastInfo.Magic = "EASYMULTI_V1";

        try
        {
            _broadcaster = new UdpClient();
            _broadcaster.EnableBroadcast = true;
            _broadcasting = true;
            _broadcastTimer = 0;
            GD.Print($"[UdpBroadcastDiscovery] 开始广播房间: {info.HostName} ({info.GameType}), 端口: {_config.BroadcastPort}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 启动广播失败: {ex.Message}");
            _broadcaster?.Close();
            _broadcaster = null;
            _broadcasting = false;
        }
    }

    /// <inheritdoc />
    public void StopBroadcast()
    {
        _broadcasting = false;
        try
        {
            _broadcaster?.Close();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 关闭广播时异常: {ex.Message}");
        }
        _broadcaster = null;
        _broadcastInfo = null;
        GD.Print("[UdpBroadcastDiscovery] 已停止广播");
    }

    // ── 监听端 API ──

    /// <inheritdoc />
    public void StartListening()
    {
        if (_listening) StopListening();

        CollectLocalIps();

        try
        {
            _listener = new UdpClient(_config.BroadcastPort);
            _listener.EnableBroadcast = true;
            _listening = true;
            _discoveredRooms.Clear();
            BeginReceive();
            GD.Print($"[UdpBroadcastDiscovery] 开始监听房间广播, 端口: {_config.BroadcastPort}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 启动监听失败: {ex.Message}");
            _listener?.Close();
            _listener = null;
            _listening = false;
        }
    }

    /// <inheritdoc />
    public void StopListening()
    {
        _listening = false;
        try
        {
            _listener?.Close();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 关闭监听时异常: {ex.Message}");
        }
        _listener = null;
        _discoveredRooms.Clear();
        GD.Print("[UdpBroadcastDiscovery] 已停止监听");
    }

    // ── Node 生命周期 ──

    /// <summary>
    /// 每帧处理：Host 端定期发送广播，Client 端清理超时房间。
    /// </summary>
    public override void _Process(double delta)
    {
        // Host 端定期广播
        if (_broadcasting && _broadcastInfo != null && _broadcaster != null)
        {
            _broadcastTimer += (float)delta;
            if (_broadcastTimer >= _config.BroadcastInterval)
            {
                _broadcastTimer = 0;
                SendBroadcast();
            }
        }

        // Client 端清理超时房间
        if (_listening)
        {
            CleanupStaleRooms();
        }
    }

    /// <summary>
    /// 节点退出场景树时清理资源。
    /// </summary>
    public override void _ExitTree()
    {
        StopBroadcast();
        StopListening();
    }

    // ── 内部逻辑 ──

    /// <summary>
    /// 发送一次 UDP 广播包。
    /// </summary>
    private void SendBroadcast()
    {
        try
        {
            var json = JsonSerializer.Serialize(_broadcastInfo);
            var data = Encoding.UTF8.GetBytes(json);
            var endpoint = new IPEndPoint(IPAddress.Broadcast, _config.BroadcastPort);
            _broadcaster!.Send(data, data.Length, endpoint);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 广播发送失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 开始异步接收 UDP 数据。
    /// </summary>
    private void BeginReceive()
    {
        if (!_listening || _listener == null) return;

        try
        {
            _listener.BeginReceive(OnReceive, null);
        }
        catch (ObjectDisposedException)
        {
            // 监听已停止，忽略
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 开始接收失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 异步接收回调。在后台线程执行，通过 CallDeferred 切回主线程。
    /// </summary>
    private void OnReceive(IAsyncResult result)
    {
        if (!_listening || _listener == null) return;

        try
        {
            IPEndPoint? endpoint = new IPEndPoint(IPAddress.Any, 0);
            var data = _listener.EndReceive(result, ref endpoint);
            var json = Encoding.UTF8.GetString(data);

            // 先做简单的 Magic 检查，避免不必要的反序列化
            if (json.Contains("EASYMULTI_V1") && endpoint?.Address != null)
            {
                var ip = endpoint.Address.ToString();
                CallDeferred(MethodName.HandleRoomFound, ip, json);
            }
        }
        catch (ObjectDisposedException)
        {
            return; // 监听已停止
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 接收数据失败: {ex.Message}");
        }

        // 继续接收下一个包
        BeginReceive();
    }

    /// <summary>
    /// 本机 IP 地址集合，用于过滤自身广播。
    /// </summary>
    private readonly HashSet<string> _localIps = new();

    /// <summary>
    /// 收集本机所有 IP 地址。
    /// </summary>
    private void CollectLocalIps()
    {
        _localIps.Clear();
        _localIps.Add("127.0.0.1");
        _localIps.Add("::1");
        try
        {
            var hostName = Dns.GetHostName();
            var addresses = Dns.GetHostAddresses(hostName);
            foreach (var addr in addresses)
            {
                _localIps.Add(addr.ToString());
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 获取本机 IP 失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 在主线程中处理发现的房间。由 CallDeferred 调用。
    /// </summary>
    /// <param name="ip">房间主机 IP 地址。</param>
    /// <param name="json">房间信息 JSON 字符串。</param>
    private void HandleRoomFound(string ip, string json)
    {
        // 过滤自身广播
        if (_localIps.Contains(ip)) return;

        RoomInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<RoomInfo>(json);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[UdpBroadcastDiscovery] 反序列化房间信息失败: {ex.Message}");
            return;
        }

        if (info == null || info.Magic != "EASYMULTI_V1") return;

        var key = $"{ip}:{info.Port}";
        var isNew = !_discoveredRooms.ContainsKey(key);

        var room = new DiscoveredRoom
        {
            Info = info,
            HostIp = ip,
            LastSeen = Time.GetUnixTimeFromSystem()
        };

        _discoveredRooms[key] = room;

        if (isNew)
        {
            GD.Print($"[UdpBroadcastDiscovery] 发现房间: {info.HostName} @ {ip}:{info.Port}");
            RoomFound?.Invoke(room);
        }

        RoomListUpdated?.Invoke();
    }

    /// <summary>
    /// 清理超时未收到广播的房间。
    /// </summary>
    private void CleanupStaleRooms()
    {
        var now = Time.GetUnixTimeFromSystem();
        var staleKeys = new List<string>();

        foreach (var (key, room) in _discoveredRooms)
        {
            if (now - room.LastSeen > _config.RoomTimeout)
            {
                staleKeys.Add(key);
            }
        }

        foreach (var key in staleKeys)
        {
            _discoveredRooms.Remove(key);
            GD.Print($"[UdpBroadcastDiscovery] 房间超时移除: {key}");
            RoomLost?.Invoke(key);
        }

        if (staleKeys.Count > 0)
        {
            RoomListUpdated?.Invoke();
        }
    }
}
