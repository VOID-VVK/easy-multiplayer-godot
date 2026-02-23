using System;
using System.Collections.Generic;

namespace EasyMultiplayer.Discovery;

/// <summary>
/// 房间信息数据类，作为广播载荷在网络中传输。
/// </summary>
/// <remarks>
/// 新增 <see cref="Metadata"/> 字典，
/// 使用者可存放自定义数据（如游戏模式、地图名）而无需修改插件。
/// Magic 标识为 <c>EASYMULTI_V1</c>，用于过滤非本插件的广播包。
/// </remarks>
public class RoomInfo
{
    /// <summary>广播魔数，用于过滤非本插件的广播包。</summary>
    public string Magic { get; set; } = "EASYMULTI_V1";

    /// <summary>房间名称 / 主机名。</summary>
    public string HostName { get; set; } = "";

    /// <summary>游戏类型标识。</summary>
    public string GameType { get; set; } = "";

    /// <summary>当前玩家数。</summary>
    public int PlayerCount { get; set; } = 1;

    /// <summary>最大玩家数。</summary>
    public int MaxPlayers { get; set; } = 2;

    /// <summary>游戏端口。</summary>
    public int Port { get; set; } = 27015;

    /// <summary>游戏版本号。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>自定义元数据字典，使用者可存放任意键值对。</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// 发现的房间条目，包含房间信息和发现时的元数据。
/// </summary>
public class DiscoveredRoom
{
    /// <summary>房间信息。</summary>
    public RoomInfo Info { get; set; } = new();

    /// <summary>主机 IP 地址。</summary>
    public string HostIp { get; set; } = "";

    /// <summary>最后一次收到该房间广播的时间戳（引擎时间）。</summary>
    public double LastSeen { get; set; }
}

/// <summary>
/// 房间发现层抽象接口。所有发现实现（UDP 广播、Lobby 服务器等）均需实现此接口。
/// </summary>
/// <remarks>
/// <para>
/// 广播端（Host）通过 <see cref="StartBroadcast"/> 定期发送房间信息，
/// 监听端（Client）通过 <see cref="StartListening"/> 接收并维护可用房间列表。
/// </para>
/// <para>
/// 超时时间和广播间隔通过 <see cref="Core.EasyMultiplayerConfig"/> 配置，
/// 不再硬编码。
/// </para>
/// </remarks>
public interface IDiscovery
{
    // ── 广播端（Host） ──

    /// <summary>
    /// 开始广播房间信息。
    /// </summary>
    /// <param name="info">要广播的房间信息。</param>
    void StartBroadcast(RoomInfo info);

    /// <summary>
    /// 停止广播。
    /// </summary>
    void StopBroadcast();

    /// <summary>当前是否正在广播。</summary>
    bool IsBroadcasting { get; }

    // ── 监听端（Client） ──

    /// <summary>
    /// 开始监听房间广播。
    /// </summary>
    void StartListening();

    /// <summary>
    /// 停止监听。
    /// </summary>
    void StopListening();

    /// <summary>当前是否正在监听。</summary>
    bool IsListening { get; }

    /// <summary>
    /// 当前发现的房间列表。键为 <c>"ip:port"</c> 格式。
    /// </summary>
    IReadOnlyDictionary<string, DiscoveredRoom> Rooms { get; }

    // ── 事件 ──

    /// <summary>发现新房间时触发。</summary>
    event Action<DiscoveredRoom> RoomFound;

    /// <summary>房间超时消失时触发。参数为房间键（<c>"ip:port"</c>）。</summary>
    event Action<string> RoomLost;

    /// <summary>房间列表发生变化时触发（新增、更新或移除）。</summary>
    event Action RoomListUpdated;
}
