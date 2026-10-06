/*
 * 檔案：TcpDeviceConnection.cs
 * 專案：Controller.Core
 * 功能：以 TCP 實作 IDeviceConnection，負責連線、逾時判斷、送出訊息、
 *       背景接收迴圈（搭配 LineFramer 做封包切割）與斷線偵測。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.2（Slice 1 送指令、收回應）
 */

using System.Net.Sockets;
using System.Text;
using Controller.Core.Protocol;

namespace Controller.Core.Connection;

/// <summary>
/// 透過 TCP 連線到設備的實作。
/// </summary>
/// <remarks>
/// 函式庫內的 await 一律加上 <c>ConfigureAwait(false)</c>，不依賴呼叫端的 UI 執行緒，
/// 避免在 UI 執行緒同步等待時發生死結。
/// </remarks>
public sealed class TcpDeviceConnection : IDeviceConnection
{
    /// <summary>設備的主機名稱或 IP。</summary>
    private readonly string _host;

    /// <summary>設備的 TCP port。</summary>
    private readonly int _port;

    /// <summary>連線逾時時間。</summary>
    private readonly TimeSpan _connectTimeout;

    /// <summary>送出鎖：同一時間只允許一個人寫入，避免兩筆訊息的位元組交錯。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>目前的 TCP 連線；尚未連線時為 null。</summary>
    private TcpClient? _client;

    /// <summary>目前連線的網路串流；尚未連線時為 null。</summary>
    private NetworkStream? _stream;

    /// <summary>用來停止背景接收迴圈的取消來源。</summary>
    private CancellationTokenSource? _receiveCts;

    /// <summary>背景接收迴圈的工作。</summary>
    private Task? _receiveTask;

    /// <summary>連線狀態旗標；會被背景執行緒修改，所以加上 volatile。</summary>
    private volatile bool _connected;

    /// <summary>
    /// 建立 TCP 設備連線物件（此時尚未真正連線）。
    /// </summary>
    /// <param name="host">設備的主機名稱或 IP，例如 "127.0.0.1"。</param>
    /// <param name="port">設備的 TCP port，範圍 1～65535。</param>
    /// <param name="connectTimeout">連線逾時時間；未指定時預設 5 秒。</param>
    /// <exception cref="ArgumentException">host 為空白時拋出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">port 超出 1～65535 時拋出。</exception>
    public TcpDeviceConnection(string host, int port, TimeSpan? connectTimeout = null)
    {
        // 參數防呆：在建構時就擋下錯誤設定，而不是等到連線才失敗
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _host = host;
        _port = port;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// 取得目前是否已連線。
    /// </summary>
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public event Action<string>? LineReceived;

    /// <inheritdoc />
    public event Action<Exception?>? Disconnected;

    /// <summary>
    /// 非同步連線到設備，成功後啟動背景接收迴圈。
    /// </summary>
    /// <param name="cancellationToken">由呼叫端取消連線的權杖。</param>
    /// <returns>代表連線動作的非同步工作。</returns>
    /// <exception cref="InvalidOperationException">已經連線時再次呼叫。</exception>
    /// <exception cref="TimeoutException">超過逾時時間仍未連上。</exception>
    /// <exception cref="SocketException">對方拒絕連線或網路無法到達。</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            throw new InvalidOperationException("已經連線。");

        // 清掉上一次被對方斷掉的殘留資源
        await DisconnectAsync().ConfigureAwait(false);

        var client = new TcpClient();

        // 把「外部取消」和「逾時」合併成一個權杖，兩者任一發生就中止連線
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_connectTimeout);

        try
        {
            await client.ConnectAsync(_host, _port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 不是外部取消，那就是逾時：轉成語意清楚的 TimeoutException
            client.Dispose();
            throw new TimeoutException(
                $"連線 {_host}:{_port} 逾時（{_connectTimeout.TotalSeconds} 秒）。");
        }
        catch
        {
            // 其他錯誤（例如對方拒絕連線）：釋放資源後原樣往上拋
            client.Dispose();
            throw;
        }

        _client = client;
        _stream = client.GetStream();
        _connected = true;

        // 接收迴圈在背景一直跑：資料隨時可能從設備進來，不是使用者按按鈕才發生
        _receiveCts = new CancellationTokenSource();
        NetworkStream stream = _stream;
        CancellationToken token = _receiveCts.Token;
        _receiveTask = Task.Run(() => ReceiveLoopAsync(stream, token));
    }

    /// <summary>
    /// 非同步送出一筆訊息，會自動在結尾補上 \n。
    /// </summary>
    /// <param name="line">要送出的訊息內容（不含 \n）。</param>
    /// <param name="cancellationToken">用來取消送出動作的權杖。</param>
    /// <returns>代表送出動作的非同步工作。</returns>
    /// <exception cref="ArgumentNullException">line 為 null。</exception>
    /// <exception cref="InvalidOperationException">尚未連線。</exception>
    public async Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        NetworkStream stream = _stream ?? throw new InvalidOperationException("尚未連線。");
        if (!_connected) throw new InvalidOperationException("尚未連線。");

        byte[] data = Encoding.UTF8.GetBytes(line + "\n");

        // 同一時間只允許一個人寫，避免兩筆訊息的位元組交錯在一起
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 背景接收迴圈：持續讀取資料，用 <see cref="LineFramer"/> 切成完整訊息後觸發 <see cref="LineReceived"/>。
    /// 對方關閉或發生錯誤時結束迴圈並觸發 <see cref="Disconnected"/>。
    /// </summary>
    /// <param name="stream">要讀取的網路串流。</param>
    /// <param name="ct">自己中斷連線時用來停止迴圈的取消權杖。</param>
    /// <returns>代表接收迴圈的非同步工作。</returns>
    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var framer = new LineFramer();
        var buffer = new byte[1024];
        Exception? error = null;

        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);

                // 讀到 0 byte 代表對方正常關閉連線
                if (n == 0) break;

                // 一次讀到的資料可能是半筆、一筆或多筆，交給 LineFramer 切割
                foreach (string line in framer.Append(buffer.AsSpan(0, n)))
                    LineReceived?.Invoke(line);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 我們自己呼叫 DisconnectAsync 造成的，不算斷線，不觸發事件
            return;
        }
        catch (Exception ex)
        {
            // 網路異常、封包格式錯誤等
            error = ex;
        }

        _connected = false;
        stream.Dispose();   // 關閉 socket
        Disconnected?.Invoke(error);
    }

    /// <summary>
    /// 中斷連線：停止接收迴圈並釋放所有資源；尚未連線時呼叫也不會出錯。
    /// </summary>
    /// <returns>代表中斷動作的非同步工作。</returns>
    public async Task DisconnectAsync()
    {
        _connected = false;
        _receiveCts?.Cancel();   // 先通知接收迴圈「這是自己要斷的」
        _client?.Dispose();

        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch { /* 接收迴圈的例外已在迴圈內處理 */ }
        }

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
        _stream = null;
        _client = null;
    }

    /// <summary>
    /// 釋放連線資源，等同 <see cref="DisconnectAsync"/>，供 <c>await using</c> 使用。
    /// </summary>
    /// <returns>代表釋放動作的非同步工作。</returns>
    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
