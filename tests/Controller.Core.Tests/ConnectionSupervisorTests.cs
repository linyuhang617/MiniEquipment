/*
 * 檔案：ConnectionSupervisorTests.cs
 * 專案：Controller.Core.Tests
 * 功能：ConnectionSupervisor（心跳與自動重連）的單元測試。
 *       用假連線與很短的時間設定（毫秒級），不用真的等 5 秒心跳、30 秒重連，
 *       驗證：重連間隔計算、第一次連線失敗、心跳正常、設備斷線後重連、
 *       心跳連續沒回應後重連、重連失敗會繼續重試、重連中可以停止、未連線不能送指令。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.3（Slice 2 斷線重連）
 */

using Controller.Core.Connection;
using Controller.Core.Tests.Fakes;

namespace Controller.Core.Tests;

/// <summary>
/// <see cref="ConnectionSupervisor"/> 的單元測試。
/// </summary>
public class ConnectionSupervisorTests
{
    /// <summary>測試用的快速設定：心跳 50 毫秒一次，重連從 10 毫秒開始，最多 40 毫秒。</summary>
    private static readonly ConnectionSupervisorOptions FastOptions = new()
    {
        HeartbeatInterval = TimeSpan.FromMilliseconds(50),
        HeartbeatTimeout = TimeSpan.FromMilliseconds(50),
        MaxMissedHeartbeats = 2,
        InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(40),
    };

    /// <summary>
    /// 重連間隔應為 1、2、4、8、16 秒，之後固定在上限 30 秒。
    /// </summary>
    [Fact]
    public void GetReconnectDelay_DoublesUntilMax()
    {
        var initial = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(30);

        double[] seconds = Enumerable.Range(1, 8)
            .Select(attempt => ConnectionSupervisor.GetReconnectDelay(attempt, initial, max).TotalSeconds)
            .ToArray();

        Assert.Equal<double>(new double[] { 1, 2, 4, 8, 16, 30, 30, 30 }, seconds);
    }

    /// <summary>
    /// 重連次數非常大時不應溢位，仍維持在上限。
    /// </summary>
    [Fact]
    public void GetReconnectDelay_HugeAttempt_StaysAtMax()
    {
        TimeSpan delay = ConnectionSupervisor.GetReconnectDelay(
            10_000, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    /// <summary>
    /// 第一次連線失敗應直接拋出例外、不自動重試，狀態維持未連線。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task StartAsync_FirstConnectFails_ThrowsAndStaysDisconnected()
    {
        var factory = new FakeFactory(callNumber =>
            new FakeDeviceConnection { ConnectFailure = new IOException("模擬連線失敗") });
        await using var supervisor = new ConnectionSupervisor(factory.Create, FastOptions);

        await Assert.ThrowsAsync<IOException>(() => supervisor.StartAsync());

        Assert.Equal(ConnectionState.Disconnected, supervisor.State);
        await Task.Delay(100);
        Assert.Equal(1, factory.CallCount);   // 沒有自動重試
    }

    /// <summary>
    /// 心跳都有回應時，應保持同一條連線，不會重連。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task HeartbeatsAnswered_StaysConnectedWithoutReconnect()
    {
        var factory = new FakeFactory(_ => new FakeDeviceConnection());
        await using var supervisor = new ConnectionSupervisor(factory.Create, FastOptions);

        await supervisor.StartAsync();
        await Task.Delay(300);   // 約 6 次心跳的時間

        Assert.Equal(ConnectionState.Connected, supervisor.State);
        Assert.Equal(1, factory.CallCount);
        Assert.True(factory.Created[0].SentLines.Count(line => line == "PING") >= 2);
    }

    /// <summary>
    /// 設備關閉連線時，應自動建立新連線並恢復成已連線，舊連線被釋放。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task DeviceDisconnects_ReconnectsWithNewConnection()
    {
        var factory = new FakeFactory(_ => new FakeDeviceConnection());
        var states = new StateRecorder();
        await using var supervisor = new ConnectionSupervisor(factory.Create, FastOptions);
        supervisor.StatusChanged += states.Record;

        await supervisor.StartAsync();
        factory.Created[0].SimulateDisconnect();

        Assert.True(await WaitUntilAsync(() =>
            factory.CallCount == 2 && supervisor.State == ConnectionState.Connected));
        Assert.True(factory.Created[0].IsDisposed);
        Assert.True(states.Contains(ConnectionState.Reconnecting));
    }

    /// <summary>
    /// 設備當機（連線還在但心跳不回應）時，連續 2 次沒回應應判定斷線並重連。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task HeartbeatNotAnswered_ReconnectsAfterMaxMissed()
    {
        // 第一條連線不回心跳（模擬當機），之後的連線正常
        var factory = new FakeFactory(callNumber =>
            new FakeDeviceConnection { RespondToHeartbeat = callNumber != 1 });
        var states = new StateRecorder();
        await using var supervisor = new ConnectionSupervisor(factory.Create, FastOptions);
        supervisor.StatusChanged += states.Record;

        await supervisor.StartAsync();

        Assert.True(await WaitUntilAsync(() =>
            factory.CallCount == 2 && supervisor.State == ConnectionState.Connected));
        Assert.True(states.ContainsMessage("連續 2 次心跳沒有回應"));
    }

    /// <summary>
    /// 重連失敗時應繼續重試，直到成功。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task ReconnectFails_KeepsRetryingUntilSuccess()
    {
        // 第 1 次（啟動）成功、第 2、3 次（重連）失敗、第 4 次成功
        var factory = new FakeFactory(callNumber => callNumber is 2 or 3
            ? new FakeDeviceConnection { ConnectFailure = new IOException("機台還沒開") }
            : new FakeDeviceConnection());
        await using var supervisor = new ConnectionSupervisor(factory.Create, FastOptions);

        await supervisor.StartAsync();
        factory.Created[0].SimulateDisconnect();

        Assert.True(await WaitUntilAsync(() =>
            factory.CallCount == 4 && supervisor.State == ConnectionState.Connected));
    }

    /// <summary>
    /// 重連等待中呼叫 StopAsync，應立刻停止、不再重連，狀態變為未連線。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task StopAsync_WhileReconnecting_StopsRetrying()
    {
        // 重連間隔設很長，確保呼叫 StopAsync 時一定還在等待
        var options = FastOptions with { InitialReconnectDelay = TimeSpan.FromSeconds(10) };
        var factory = new FakeFactory(_ => new FakeDeviceConnection());
        var supervisor = new ConnectionSupervisor(factory.Create, options);

        await supervisor.StartAsync();
        factory.Created[0].SimulateDisconnect();
        Assert.True(await WaitUntilAsync(() => supervisor.State == ConnectionState.Reconnecting));

        await supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ConnectionState.Disconnected, supervisor.State);
        Assert.Equal(1, factory.CallCount);
    }

    /// <summary>
    /// 尚未連線時送指令，應拋出 InvalidOperationException。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task SendCommandAsync_NotConnected_Throws()
    {
        await using var supervisor = new ConnectionSupervisor(() => new FakeDeviceConnection(), FastOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.SendCommandAsync("GET STATUS", TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// 已連線時，指令應透過目前的連線送出並收到回應。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task SendCommandAsync_Connected_ReturnsReply()
    {
        var factory = new FakeFactory(_ =>
        {
            var fake = new FakeDeviceConnection();
            fake.LineSent += line =>
            {
                if (line == "GET STATUS") fake.SimulateReceive("OK IDLE");
            };
            return fake;
        });
        await using var supervisor = new ConnectionSupervisor(factory.Create, FastOptions);
        await supervisor.StartAsync();

        string reply = await supervisor.SendCommandAsync("GET STATUS", TimeSpan.FromSeconds(1));

        Assert.Equal("OK IDLE", reply);
    }

    /// <summary>
    /// 每隔 10 毫秒檢查一次條件，直到成立或超過時間。
    /// </summary>
    /// <param name="condition">要等待成立的條件。</param>
    /// <param name="timeoutMilliseconds">最長等待時間（毫秒），預設 5 秒。</param>
    /// <returns>條件在時間內成立時為 true。</returns>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// 假連線工廠：記錄被呼叫幾次、建立了哪些假連線，並依「第幾次呼叫」決定假連線的行為。
    /// </summary>
    private sealed class FakeFactory
    {
        /// <summary>保護內部清單的鎖物件（重連在背景執行緒呼叫工廠）。</summary>
        private readonly object _gate = new();

        /// <summary>依呼叫次數（從 1 開始）建立假連線的函式。</summary>
        private readonly Func<int, FakeDeviceConnection> _build;

        /// <summary>所有建立過的假連線。</summary>
        private readonly List<FakeDeviceConnection> _created = new();

        /// <summary>
        /// 建立假連線工廠。
        /// </summary>
        /// <param name="build">依呼叫次數（從 1 開始）建立假連線的函式。</param>
        public FakeFactory(Func<int, FakeDeviceConnection> build) => _build = build;

        /// <summary>取得工廠被呼叫的次數。</summary>
        public int CallCount
        {
            get { lock (_gate) return _created.Count; }
        }

        /// <summary>取得所有建立過的假連線快照。</summary>
        public IReadOnlyList<FakeDeviceConnection> Created
        {
            get { lock (_gate) return _created.ToArray(); }
        }

        /// <summary>
        /// 建立一個假連線，傳給 <see cref="ConnectionSupervisor"/> 當作連線工廠。
        /// </summary>
        /// <returns>新的假連線。</returns>
        public IDeviceConnection Create()
        {
            lock (_gate)
            {
                FakeDeviceConnection fake = _build(_created.Count + 1);
                _created.Add(fake);
                return fake;
            }
        }
    }

    /// <summary>
    /// 記錄連線監控器發出的所有狀態與訊息（可能在背景執行緒被呼叫，所以用鎖保護）。
    /// </summary>
    private sealed class StateRecorder
    {
        /// <summary>保護清單的鎖物件。</summary>
        private readonly object _gate = new();

        /// <summary>記錄到的狀態與訊息。</summary>
        private readonly List<(ConnectionState State, string Message)> _entries = new();

        /// <summary>
        /// 記錄一筆狀態，可直接訂閱 <see cref="ConnectionSupervisor.StatusChanged"/>。
        /// </summary>
        /// <param name="state">狀態。</param>
        /// <param name="message">說明文字。</param>
        public void Record(ConnectionState state, string message)
        {
            lock (_gate) _entries.Add((state, message));
        }

        /// <summary>
        /// 是否曾經出現指定的狀態。
        /// </summary>
        /// <param name="state">要檢查的狀態。</param>
        /// <returns>出現過時為 true。</returns>
        public bool Contains(ConnectionState state)
        {
            lock (_gate) return _entries.Any(entry => entry.State == state);
        }

        /// <summary>
        /// 是否曾經出現包含指定文字的訊息。
        /// </summary>
        /// <param name="text">要尋找的文字。</param>
        /// <returns>出現過時為 true。</returns>
        public bool ContainsMessage(string text)
        {
            lock (_gate) return _entries.Any(entry => entry.Message.Contains(text));
        }
    }
}
