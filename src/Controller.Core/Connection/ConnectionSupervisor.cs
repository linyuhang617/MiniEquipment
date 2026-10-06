/*
 * 檔案：ConnectionSupervisor.cs
 * 專案：Controller.Core
 * 功能：連線監控器。負責「連上之後一直保持連線」：
 *       1. 心跳：每 5 秒送一次 PING，連續 2 次沒回應就判定斷線。
 *          用來偵測「線還在但設備當機」或「網路線被拔掉」這種收不到斷線通知的情況。
 *       2. 斷線偵測：設備主動關閉連線時立即察覺，不用等心跳。
 *       3. 自動重連：斷線後依 1、2、4、8… 秒的間隔（指數退避，最多 30 秒）不斷重試，
 *          避免設備還沒起來時一直狂連，造成網路與設備負擔。
 *       連線物件由外部傳入的工廠建立，所以可以換成序列埠，也可以在單元測試換成假連線。
 *       Slice 3 起實作 ICommandSender，供機台控制器（MachineController）送指令。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3：實作 ICommandSender）
 */

using Controller.Core.Protocol;

namespace Controller.Core.Connection;

/// <summary>
/// 監控連線、送心跳、斷線後自動重連的連線監控器。
/// </summary>
/// <remarks>
/// 事件 <see cref="StatusChanged"/> 與 <see cref="UnsolicitedLineReceived"/> 可能在背景執行緒觸發，
/// 訂閱者若要更新畫面，必須自行切回 UI 執行緒。
/// </remarks>
public sealed class ConnectionSupervisor : IAsyncDisposable, ICommandSender
{
    /// <summary>每次（重新）連線時用來建立新連線物件的工廠。</summary>
    private readonly Func<IDeviceConnection> _connectionFactory;

    /// <summary>心跳與重連的設定值。</summary>
    private readonly ConnectionSupervisorOptions _options;

    /// <summary>目前的連線；未連線時為 null。</summary>
    private IDeviceConnection? _connection;

    /// <summary>目前的指令收發用戶端；未連線時為 null。會被背景執行緒替換，所以加上 volatile。</summary>
    private volatile DeviceClient? _client;

    /// <summary>目前連線的「斷線通知」；設備關閉連線時會被設定為斷線原因。</summary>
    private TaskCompletionSource<string>? _connectionLost;

    /// <summary>用來停止背景監控迴圈的取消來源。</summary>
    private CancellationTokenSource? _stopCts;

    /// <summary>背景監控迴圈的工作。</summary>
    private Task? _runTask;

    /// <summary>目前的連線狀態。會被背景執行緒修改，所以加上 volatile。</summary>
    private volatile ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>
    /// 建立連線監控器（此時尚未連線）。
    /// </summary>
    /// <param name="connectionFactory">建立新連線物件的工廠，例如 <c>() =&gt; new TcpDeviceConnection(host, port)</c>。</param>
    /// <param name="options">心跳與重連設定；未指定時使用預設值。</param>
    /// <exception cref="ArgumentNullException">connectionFactory 為 null。</exception>
    public ConnectionSupervisor(Func<IDeviceConnection> connectionFactory, ConnectionSupervisorOptions? options = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _options = options ?? new ConnectionSupervisorOptions();
    }

    /// <summary>
    /// 取得目前的連線狀態。
    /// </summary>
    public ConnectionState State => _state;

    /// <summary>
    /// 連線狀態改變或有值得記錄的事情（例如漏掉一次心跳）時觸發，參數為狀態與說明文字。
    /// </summary>
    public event Action<ConnectionState, string>? StatusChanged;

    /// <summary>
    /// 收到非預期訊息時觸發，例如逾時後才到的回應。
    /// </summary>
    public event Action<string>? UnsolicitedLineReceived;

    /// <summary>
    /// 計算第幾次重連前要等多久：起始間隔乘上 2 的 (次數 - 1) 次方，最多不超過上限。
    /// 例如起始 1 秒、上限 30 秒：1、2、4、8、16、30、30…秒。
    /// </summary>
    /// <param name="attempt">第幾次重連，從 1 開始。</param>
    /// <param name="initialDelay">第一次重連前的等待時間。</param>
    /// <param name="maxDelay">等待時間的上限。</param>
    /// <returns>這一次重連前要等待的時間。</returns>
    /// <exception cref="ArgumentOutOfRangeException">attempt 小於 1。</exception>
    public static TimeSpan GetReconnectDelay(int attempt, TimeSpan initialDelay, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        // 次數很大時 2 的次方會溢位，先限制指數；反正結果一定會被上限截斷
        int exponent = Math.Min(attempt - 1, 30);
        double milliseconds = initialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, maxDelay.TotalMilliseconds));
    }

    /// <summary>
    /// 第一次連線，成功後啟動背景監控（心跳與自動重連）。
    /// </summary>
    /// <param name="cancellationToken">用來取消第一次連線的權杖。</param>
    /// <returns>代表第一次連線的非同步工作。</returns>
    /// <exception cref="InvalidOperationException">已經啟動過。</exception>
    /// <remarks>
    /// 第一次連線失敗會直接拋出例外、不會自動重試，讓使用者立刻知道 IP 或 Port 是否設定錯誤。
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_runTask is not null || _state == ConnectionState.Connecting)
            throw new InvalidOperationException("已經啟動。");

        SetState(ConnectionState.Connecting, "連線中…");

        try
        {
            await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 第一次就失敗：直接回到未連線，由呼叫端顯示錯誤（這裡不發事件，避免訊息重複）
            _state = ConnectionState.Disconnected;
            throw;
        }

        SetState(ConnectionState.Connected, "已連線");

        // 背景監控迴圈：一直跑到 StopAsync 被呼叫
        _stopCts = new CancellationTokenSource();
        CancellationToken stopToken = _stopCts.Token;
        _runTask = Task.Run(() => RunAsync(stopToken));
    }

    /// <summary>
    /// 停止監控與重連，並中斷目前連線。
    /// </summary>
    /// <returns>代表停止動作的非同步工作。</returns>
    public async Task StopAsync()
    {
        CancellationTokenSource? stopCts = _stopCts;
        Task? runTask = _runTask;
        _stopCts = null;
        _runTask = null;

        if (stopCts is not null)
        {
            // 通知背景迴圈結束，並等它真的結束，避免它在我們清理資源時又連上
            stopCts.Cancel();
            if (runTask is not null)
            {
                try { await runTask.ConfigureAwait(false); }
                catch { /* 背景迴圈的例外已在迴圈內處理 */ }
            }
            stopCts.Dispose();
        }

        await DropConnectionAsync().ConfigureAwait(false);

        if (_state != ConnectionState.Disconnected)
            SetState(ConnectionState.Disconnected, "已中斷連線");
    }

    /// <summary>
    /// 送出一筆指令並等待回應。
    /// </summary>
    /// <param name="command">要送出的指令，例如 "GET STATUS"。</param>
    /// <param name="timeout">等待回應的最長時間。</param>
    /// <param name="cancellationToken">用來取消等待的權杖。</param>
    /// <returns>設備回應的訊息內容。</returns>
    /// <exception cref="InvalidOperationException">目前沒有連線（例如正在重連）。</exception>
    /// <exception cref="TimeoutException">超過 timeout 仍未收到回應。</exception>
    /// <exception cref="IOException">等待回應時連線中斷。</exception>
    public Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DeviceClient client = _client ?? throw new InvalidOperationException("目前沒有連線，無法送出指令。");
        return client.SendCommandAsync(command, timeout, cancellationToken);
    }

    /// <summary>
    /// 釋放資源，等同 <see cref="StopAsync"/>，供 <c>await using</c> 使用。
    /// </summary>
    /// <returns>代表釋放動作的非同步工作。</returns>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// 背景監控迴圈：已連線時監控心跳，判定斷線後自動重連，重連成功再繼續監控。
    /// </summary>
    /// <param name="stopToken">StopAsync 被呼叫時會取消的權杖。</param>
    /// <returns>代表監控迴圈的非同步工作。</returns>
    private async Task RunAsync(CancellationToken stopToken)
    {
        try
        {
            while (true)
            {
                // 1. 監控目前連線，直到判定斷線為止
                string reason = await MonitorAsync(stopToken).ConfigureAwait(false);

                // 2. 丟掉壞掉的連線（不再沿用，下一次一律建立新的連線物件）
                await DropConnectionAsync().ConfigureAwait(false);
                SetState(ConnectionState.Reconnecting, $"{reason}，開始自動重連");

                // 3. 重連到成功為止
                await ReconnectAsync(stopToken).ConfigureAwait(false);
                SetState(ConnectionState.Connected, "重連成功");
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // StopAsync 造成的取消，屬於正常結束
        }
    }

    /// <summary>
    /// 監控目前連線：定期送心跳，直到設備關閉連線或連續漏掉太多次心跳。
    /// </summary>
    /// <param name="stopToken">StopAsync 被呼叫時會取消的權杖。</param>
    /// <returns>判定斷線的原因。</returns>
    /// <exception cref="OperationCanceledException">StopAsync 被呼叫。</exception>
    private async Task<string> MonitorAsync(CancellationToken stopToken)
    {
        DeviceClient client = _client ?? throw new InvalidOperationException("尚未連線。");
        Task<string> lostTask = (_connectionLost ?? throw new InvalidOperationException("尚未連線。")).Task;
        int missed = 0;

        while (true)
        {
            // 等到下一次心跳時間；期間如果設備關閉連線，立刻結束，不用等心跳
            Task delayTask = Task.Delay(_options.HeartbeatInterval, stopToken);
            Task finished = await Task.WhenAny(delayTask, lostTask).ConfigureAwait(false);
            if (finished == lostTask)
                return await lostTask.ConfigureAwait(false);

            // 若是 StopAsync 造成的取消，這裡會拋出 OperationCanceledException
            await delayTask.ConfigureAwait(false);

            try
            {
                await client.SendCommandAsync(_options.HeartbeatCommand, _options.HeartbeatTimeout, stopToken)
                    .ConfigureAwait(false);
                missed = 0;   // 有回應就歸零，只算「連續」漏掉的次數
            }
            catch (TimeoutException)
            {
                missed++;
                if (missed >= _options.MaxMissedHeartbeats)
                    return $"連續 {missed} 次心跳沒有回應";

                SetState(ConnectionState.Connected, $"心跳沒有回應（{missed}/{_options.MaxMissedHeartbeats}）");
            }
            catch (Exception ex) when (!stopToken.IsCancellationRequested)
            {
                // 送心跳時發現連線已壞：優先回報設備端的斷線原因
                return lostTask.IsCompleted
                    ? await lostTask.ConfigureAwait(false)
                    : $"心跳失敗：{ex.Message}";
            }
        }
    }

    /// <summary>
    /// 依指數退避的間隔不斷重連，直到成功或 StopAsync 被呼叫。
    /// </summary>
    /// <param name="stopToken">StopAsync 被呼叫時會取消的權杖。</param>
    /// <returns>代表重連過程的非同步工作，完成時表示已重新連上。</returns>
    /// <exception cref="OperationCanceledException">StopAsync 被呼叫。</exception>
    private async Task ReconnectAsync(CancellationToken stopToken)
    {
        int attempt = 0;

        while (true)
        {
            attempt++;
            TimeSpan delay = GetReconnectDelay(attempt, _options.InitialReconnectDelay, _options.MaxReconnectDelay);
            SetState(ConnectionState.Reconnecting, $"第 {attempt} 次重連，{delay.TotalSeconds:0.##} 秒後嘗試");

            await Task.Delay(delay, stopToken).ConfigureAwait(false);

            try
            {
                await ConnectOnceAsync(stopToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (!stopToken.IsCancellationRequested)
            {
                SetState(ConnectionState.Reconnecting, $"第 {attempt} 次重連失敗：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 建立一個新的連線物件並連上，成功後才替換成目前使用的連線。
    /// </summary>
    /// <param name="cancellationToken">用來取消連線的權杖。</param>
    /// <returns>代表連線動作的非同步工作。</returns>
    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        IDeviceConnection connection = _connectionFactory();
        var client = new DeviceClient(connection);
        var lost = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 設備關閉連線時，把原因寫進「斷線通知」，監控迴圈會立刻察覺
        connection.Disconnected += error =>
            lost.TrySetResult(error is null ? "機台關閉了連線" : $"連線中斷：{error.Message}");

        // 非預期訊息轉發給外部（例如畫面）
        client.UnsolicitedLineReceived += line => UnsolicitedLineReceived?.Invoke(line);

        try
        {
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _connection = connection;
        _connectionLost = lost;
        _client = client;
    }

    /// <summary>
    /// 丟掉目前的連線與用戶端並釋放資源；沒有連線時呼叫也不會出錯。
    /// </summary>
    /// <returns>代表釋放動作的非同步工作。</returns>
    private async Task DropConnectionAsync()
    {
        IDeviceConnection? connection = _connection;
        DeviceClient? client = _client;
        _connection = null;
        _client = null;
        _connectionLost = null;

        client?.Dispose();
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 更新連線狀態並觸發 <see cref="StatusChanged"/>。
    /// </summary>
    /// <param name="state">新的連線狀態。</param>
    /// <param name="message">說明文字。</param>
    private void SetState(ConnectionState state, string message)
    {
        _state = state;
        StatusChanged?.Invoke(state, message);
    }
}
