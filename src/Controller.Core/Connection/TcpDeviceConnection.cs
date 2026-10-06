/*
 * 檔案：TcpDeviceConnection.cs
 * 專案：Controller.Core
 * 功能：以 TCP 實作 IDeviceConnection，負責連線、逾時判斷與中斷連線。
 *       連線逾時會轉成 TimeoutException，讓上層顯示清楚的錯誤訊息。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.1（Slice 0 Walking Skeleton）
 */

using System.Net.Sockets;

namespace Controller.Core.Connection;

/// <summary>
/// 透過 TCP 連線到設備的實作。
/// </summary>
public sealed class TcpDeviceConnection : IDeviceConnection
{
    /// <summary>設備的主機名稱或 IP。</summary>
    private readonly string _host;

    /// <summary>設備的 TCP port。</summary>
    private readonly int _port;

    /// <summary>連線逾時時間。</summary>
    private readonly TimeSpan _connectTimeout;

    /// <summary>目前的 TCP 連線；尚未連線時為 null。</summary>
    private TcpClient? _client;

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
    public bool IsConnected => _client?.Connected == true;

    /// <summary>
    /// 非同步連線到設備；超過逾時時間仍未連上會拋出 <see cref="TimeoutException"/>。
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

        var client = new TcpClient();

        // 把「外部取消」和「逾時」合併成一個權杖，兩者任一發生就中止連線
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_connectTimeout);

        try
        {
            await client.ConnectAsync(_host, _port, timeoutCts.Token);
            _client = client;
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
    }

    /// <summary>
    /// 中斷連線並釋放資源；尚未連線時呼叫也不會出錯。
    /// </summary>
    /// <returns>已完成的工作（TCP 關閉是同步動作）。</returns>
    public Task DisconnectAsync()
    {
        _client?.Dispose();
        _client = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 釋放連線資源，等同 <see cref="DisconnectAsync"/>，供 <c>await using</c> 使用。
    /// </summary>
    /// <returns>代表釋放動作的非同步工作。</returns>
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
