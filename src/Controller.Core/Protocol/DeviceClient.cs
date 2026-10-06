/*
 * 檔案：DeviceClient.cs
 * 專案：Controller.Core
 * 功能：請求／回應處理。送出一筆指令後，把下一筆收到的訊息當作它的回應，
 *       超過指定時間沒有回應就拋出 TimeoutException。
 *       一次只允許一筆指令在等回應（與多數設備的文字協定相同），後面的指令會排隊。
 *       逾時後才到的回應會歸類為「非預期訊息」，不會被誤認成下一筆指令的回應。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.2（Slice 1 送指令、收回應）
 */

using Controller.Core.Connection;

namespace Controller.Core.Protocol;

/// <summary>
/// 建立在 <see cref="IDeviceConnection"/> 之上的指令收發用戶端。
/// </summary>
public sealed class DeviceClient : IDisposable
{
    /// <summary>底層的設備連線。</summary>
    private readonly IDeviceConnection _connection;

    /// <summary>請求鎖：確保同一時間只有一筆指令在等回應。</summary>
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    /// <summary>保護 <see cref="_pending"/> 的鎖物件（接收執行緒與呼叫端都會存取）。</summary>
    private readonly object _gate = new();

    /// <summary>目前正在等回應的指令；沒有指令在等時為 null。</summary>
    private TaskCompletionSource<string>? _pending;

    /// <summary>
    /// 建立指令收發用戶端，並訂閱連線的收訊與斷線事件。
    /// </summary>
    /// <param name="connection">底層的設備連線。</param>
    /// <exception cref="ArgumentNullException">connection 為 null。</exception>
    public DeviceClient(IDeviceConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.LineReceived += OnLineReceived;
        _connection.Disconnected += OnDisconnected;
    }

    /// <summary>
    /// 沒有指令在等回應時收到的訊息，例如逾時之後才到的回應。
    /// </summary>
    /// <remarks>
    /// Slice 4 的設備主動事件（EVT ...）也會從這裡進來。此事件在「背景執行緒」觸發。
    /// </remarks>
    public event Action<string>? UnsolicitedLineReceived;

    /// <summary>
    /// 非同步送出一筆指令並等待回應。
    /// </summary>
    /// <param name="command">要送出的指令，例如 "GET STATUS"。</param>
    /// <param name="timeout">等待回應的最長時間。</param>
    /// <param name="cancellationToken">用來取消等待的權杖。</param>
    /// <returns>設備回應的訊息內容。</returns>
    /// <exception cref="ArgumentException">command 為空白。</exception>
    /// <exception cref="TimeoutException">超過 timeout 仍未收到回應。</exception>
    /// <exception cref="IOException">等待回應時連線中斷。</exception>
    public async Task<string> SendCommandAsync(
        string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        // 排隊：前一筆指令還在等回應時，這一筆先等著
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        // RunContinuationsAsynchronously：完成回應時不要在接收執行緒上直接跑後續程式碼
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 先登記「正在等回應」再送出，避免回應比登記還快到達而被漏掉
        lock (_gate) _pending = tcs;

        try
        {
            await _connection.SendLineAsync(command, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 補上是哪一筆指令逾時，方便畫面顯示與事後追查
            throw new TimeoutException($"指令「{command}」在 {timeout.TotalSeconds} 秒內沒有回應。");
        }
        finally
        {
            // 不管成功、逾時或失敗都清掉登記，逾時後才到的回應就不會被誤認成下一筆指令的回應
            lock (_gate)
            {
                if (ReferenceEquals(_pending, tcs)) _pending = null;
            }
            _requestLock.Release();
        }
    }

    /// <summary>
    /// 收到一筆訊息時的處理：有指令在等就當作它的回應，否則當作非預期訊息轉發出去。
    /// </summary>
    /// <param name="line">收到的完整訊息。</param>
    private void OnLineReceived(string line)
    {
        TaskCompletionSource<string>? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending is not null)
            pending.TrySetResult(line);
        else
            UnsolicitedLineReceived?.Invoke(line);
    }

    /// <summary>
    /// 連線中斷時的處理：讓正在等回應的指令立刻失敗，不用等到逾時。
    /// </summary>
    /// <param name="error">造成中斷的例外；對方正常關閉時為 null。</param>
    private void OnDisconnected(Exception? error)
    {
        TaskCompletionSource<string>? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        pending?.TrySetException(new IOException("等待回應時連線中斷。", error));
    }

    /// <summary>
    /// 取消訂閱連線事件。不會關閉底層連線，連線由建立它的人負責關閉。
    /// </summary>
    public void Dispose()
    {
        _connection.LineReceived -= OnLineReceived;
        _connection.Disconnected -= OnDisconnected;
    }
}
