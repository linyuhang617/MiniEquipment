/*
 * 檔案：FakeDeviceConnection.cs
 * 專案：Controller.Core.Tests
 * 功能：測試用的假連線。不開網路，由測試程式直接控制「設備什麼時候回什麼」、
 *       連線會不會失敗、心跳有沒有回應、什麼時候斷線。
 *       Slice 2 起由 DeviceClientTests 與 ConnectionSupervisorTests 共用。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.3（Slice 2 斷線重連）
 */

using Controller.Core.Connection;

namespace Controller.Core.Tests.Fakes;

/// <summary>
/// 實作 <see cref="IDeviceConnection"/> 的假連線。
/// </summary>
/// <remarks>
/// 連線監控器會在背景執行緒送心跳，所以內部的清單用鎖保護。
/// </remarks>
internal sealed class FakeDeviceConnection : IDeviceConnection
{
    /// <summary>保護 <see cref="_sentLines"/> 的鎖物件。</summary>
    private readonly object _gate = new();

    /// <summary>所有送出過的訊息。</summary>
    private readonly List<string> _sentLines = new();

    /// <summary>
    /// 取得所有送出過的訊息快照，依送出順序排列。
    /// </summary>
    public IReadOnlyList<string> SentLines
    {
        get { lock (_gate) return _sentLines.ToArray(); }
    }

    /// <summary>
    /// 設定後，<see cref="ConnectAsync"/> 會拋出這個例外，用來模擬連線失敗。
    /// </summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>
    /// 是否自動回應心跳（收到 "PING" 時回 "OK PONG"）。預設 true；設為 false 可模擬設備當機。
    /// </summary>
    public bool RespondToHeartbeat { get; set; } = true;

    /// <summary>
    /// 取得目前是否已連線。
    /// </summary>
    public bool IsConnected { get; private set; } = true;

    /// <summary>
    /// 取得是否已被釋放（連線監控器丟掉壞連線時會呼叫 DisposeAsync）。
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <inheritdoc />
    public event Action<string>? LineReceived;

    /// <inheritdoc />
    public event Action<Exception?>? Disconnected;

    /// <summary>每送出一筆訊息就觸發，測試用來模擬設備回應。</summary>
    public event Action<string>? LineSent;

    /// <summary>
    /// 模擬連線；若有設定 <see cref="ConnectFailure"/> 則拋出該例外。
    /// </summary>
    /// <param name="cancellationToken">未使用。</param>
    /// <returns>已完成的工作。</returns>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectFailure is not null)
            throw ConnectFailure;

        IsConnected = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 模擬自己中斷連線。
    /// </summary>
    /// <returns>已完成的工作。</returns>
    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 記錄送出的訊息、觸發 <see cref="LineSent"/>，並視設定自動回應心跳。
    /// </summary>
    /// <param name="line">送出的訊息。</param>
    /// <param name="cancellationToken">未使用。</param>
    /// <returns>已完成的工作。</returns>
    public Task SendLineAsync(string line, CancellationToken cancellationToken = default)
    {
        lock (_gate) _sentLines.Add(line);
        LineSent?.Invoke(line);

        if (line == "PING" && RespondToHeartbeat)
            SimulateReceive("OK PONG");

        return Task.CompletedTask;
    }

    /// <summary>
    /// 模擬從設備收到一筆訊息。
    /// </summary>
    /// <param name="line">收到的訊息。</param>
    public void SimulateReceive(string line) => LineReceived?.Invoke(line);

    /// <summary>
    /// 模擬設備端斷線。
    /// </summary>
    /// <param name="error">造成斷線的例外；正常關閉時為 null。</param>
    public void SimulateDisconnect(Exception? error = null)
    {
        IsConnected = false;
        Disconnected?.Invoke(error);
    }

    /// <summary>
    /// 標記為已釋放。
    /// </summary>
    /// <returns>已完成的工作。</returns>
    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
