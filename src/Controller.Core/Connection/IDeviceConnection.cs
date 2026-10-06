/*
 * 檔案：IDeviceConnection.cs
 * 專案：Controller.Core
 * 功能：定義「設備連線」的共同介面。TCP、序列埠（RS232）等不同傳輸方式
 *       都實作這個介面，上層（畫面、狀態機）只依賴介面，不管資料從哪條線來。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.1（Slice 0 Walking Skeleton）
 */

namespace Controller.Core.Connection;

/// <summary>
/// 設備連線的抽象介面。
/// </summary>
/// <remarks>
/// 繼承 <see cref="IAsyncDisposable"/>，使用完畢可用 <c>await using</c> 自動釋放連線資源。
/// </remarks>
public interface IDeviceConnection : IAsyncDisposable
{
    /// <summary>
    /// 取得目前是否已連線。
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// 非同步建立與設備的連線。
    /// </summary>
    /// <param name="cancellationToken">用來取消連線動作的權杖。</param>
    /// <returns>代表連線動作的非同步工作。</returns>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 非同步中斷與設備的連線；尚未連線時呼叫也不會出錯。
    /// </summary>
    /// <returns>代表中斷動作的非同步工作。</returns>
    Task DisconnectAsync();
}
