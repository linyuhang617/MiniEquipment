/*
 * 檔案：ConnectionSupervisorOptions.cs
 * 專案：Controller.Core
 * 功能：連線監控器的設定值：心跳間隔、心跳逾時、允許連續漏掉幾次心跳、
 *       重連的起始間隔與最大間隔。預設值對應 Slice 2 的驗收條件。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.3（Slice 2 斷線重連）
 */

namespace Controller.Core.Connection;

/// <summary>
/// <see cref="ConnectionSupervisor"/> 的設定值。
/// </summary>
/// <remarks>
/// 使用 record 搭配 init，建立後不可修改；單元測試可以傳入很短的時間讓測試跑得快。
/// </remarks>
public sealed record ConnectionSupervisorOptions
{
    /// <summary>心跳指令，設備應回覆任意一行（模擬機台回 OK PONG）。預設 "PING"。</summary>
    public string HeartbeatCommand { get; init; } = "PING";

    /// <summary>每隔多久送一次心跳。預設 5 秒。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>心跳等待回應的時間。預設 2 秒。</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>連續幾次心跳沒有回應就判定斷線。預設 2 次。</summary>
    public int MaxMissedHeartbeats { get; init; } = 2;

    /// <summary>第一次重連前的等待時間，之後每次加倍。預設 1 秒。</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>重連等待時間的上限。預設 30 秒。</summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
}
