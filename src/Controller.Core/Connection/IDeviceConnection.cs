/*
 * 檔案：IDeviceConnection.cs
 * 專案：Controller.Core
 * 功能：定義「設備連線」的共同介面。TCP、序列埠（RS232）等不同傳輸方式
 *       都實作這個介面，上層（畫面、狀態機）只依賴介面，不管資料從哪條線來。
 *       協定為「一行一筆訊息，以 \n 結尾」，封包切割由實作類別負責。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.2（Slice 1 送指令、收回應）
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
    /// 收到一筆完整訊息時觸發，參數為已去掉結尾 \n 的訊息內容。
    /// </summary>
    /// <remarks>
    /// 此事件在「背景執行緒」觸發，訂閱者若要更新畫面，必須自行切回 UI 執行緒。
    /// </remarks>
    event Action<string>? LineReceived;

    /// <summary>
    /// 連線被對方關閉或異常中斷時觸發；參數為造成中斷的例外，對方正常關閉時為 null。
    /// </summary>
    /// <remarks>
    /// 自己呼叫 <see cref="DisconnectAsync"/> 不會觸發。此事件在「背景執行緒」觸發。
    /// </remarks>
    event Action<Exception?>? Disconnected;

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

    /// <summary>
    /// 非同步送出一筆訊息，會自動在結尾補上 \n。
    /// </summary>
    /// <param name="line">要送出的訊息內容（不含 \n）。</param>
    /// <param name="cancellationToken">用來取消送出動作的權杖。</param>
    /// <returns>代表送出動作的非同步工作。</returns>
    Task SendLineAsync(string line, CancellationToken cancellationToken = default);
}
