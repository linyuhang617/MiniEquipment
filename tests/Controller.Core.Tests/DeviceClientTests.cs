/*
 * 檔案：DeviceClientTests.cs
 * 專案：Controller.Core.Tests
 * 功能：DeviceClient（請求／回應）的單元測試。用假的連線（FakeDeviceConnection）
 *       取代真正的網路，精準控制「設備什麼時候回什麼」，驗證正常回應、逾時、
 *       逾時後的遲到回應、等待中斷線、非預期訊息等情況。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.2（Slice 1 送指令、收回應）
 */

using Controller.Core.Connection;
using Controller.Core.Protocol;

namespace Controller.Core.Tests;

/// <summary>
/// <see cref="DeviceClient"/> 的單元測試。
/// </summary>
public class DeviceClientTests
{
    /// <summary>測試逾時用的短時間，讓測試跑得快。</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>不預期逾時的情況使用的長時間。</summary>
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 設備有回應時，SendCommandAsync 應回傳該回應，且指令確實被送出。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task SendCommandAsync_DeviceReplies_ReturnsReply()
    {
        var fake = new FakeDeviceConnection();
        fake.LineSent += line =>
        {
            if (line == "GET STATUS") fake.SimulateReceive("OK IDLE");
        };
        using var client = new DeviceClient(fake);

        string reply = await client.SendCommandAsync("GET STATUS", LongTimeout);

        Assert.Equal("OK IDLE", reply);
        Assert.Equal<string>(new[] { "GET STATUS" }, fake.SentLines);
    }

    /// <summary>
    /// 設備沒有回應時，應在逾時後拋出 TimeoutException。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task SendCommandAsync_NoReply_ThrowsTimeout()
    {
        var fake = new FakeDeviceConnection();
        using var client = new DeviceClient(fake);

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.SendCommandAsync("SLEEP", ShortTimeout));
    }

    /// <summary>
    /// 逾時之後才到的回應，應歸類為非預期訊息，不能被當成下一筆指令的回應。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task LateReplyAfterTimeout_IsNotTakenAsNextCommandsReply()
    {
        var fake = new FakeDeviceConnection();
        using var client = new DeviceClient(fake);
        var unsolicited = new List<string>();
        client.UnsolicitedLineReceived += unsolicited.Add;

        // 第一筆指令逾時
        await Assert.ThrowsAsync<TimeoutException>(
            () => client.SendCommandAsync("SLEEP", ShortTimeout));

        // 逾時之後回應才到
        fake.SimulateReceive("OK LATE");

        // 下一筆指令應拿到自己的回應
        fake.LineSent += line =>
        {
            if (line == "GET STATUS") fake.SimulateReceive("OK IDLE");
        };
        string reply = await client.SendCommandAsync("GET STATUS", LongTimeout);

        Assert.Equal("OK IDLE", reply);
        Assert.Equal<string>(new[] { "OK LATE" }, unsolicited);
    }

    /// <summary>
    /// 等待回應時連線中斷，應立刻拋出 IOException，不用等到逾時。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task DisconnectWhileWaiting_ThrowsIOException()
    {
        var fake = new FakeDeviceConnection();
        using var client = new DeviceClient(fake);

        Task<string> waiting = client.SendCommandAsync("GET STATUS", LongTimeout);
        fake.SimulateDisconnect();

        await Assert.ThrowsAsync<IOException>(() => waiting);
    }

    /// <summary>
    /// 沒有指令在等回應時收到訊息，應觸發 UnsolicitedLineReceived。
    /// </summary>
    [Fact]
    public void LineWithoutPendingCommand_RaisesUnsolicited()
    {
        var fake = new FakeDeviceConnection();
        using var client = new DeviceClient(fake);
        var unsolicited = new List<string>();
        client.UnsolicitedLineReceived += unsolicited.Add;

        fake.SimulateReceive("EVT DOOR_OPEN");

        Assert.Equal<string>(new[] { "EVT DOOR_OPEN" }, unsolicited);
    }

    /// <summary>
    /// 測試用的假連線：不開網路，由測試程式直接控制收到的訊息與斷線時機。
    /// </summary>
    private sealed class FakeDeviceConnection : IDeviceConnection
    {
        /// <summary>取得所有送出過的訊息，依送出順序排列。</summary>
        public List<string> SentLines { get; } = new();

        /// <summary>取得目前是否已連線（預設已連線）。</summary>
        public bool IsConnected { get; private set; } = true;

        /// <inheritdoc />
        public event Action<string>? LineReceived;

        /// <inheritdoc />
        public event Action<Exception?>? Disconnected;

        /// <summary>每送出一筆訊息就觸發，測試用來模擬設備回應。</summary>
        public event Action<string>? LineSent;

        /// <summary>
        /// 模擬連線成功。
        /// </summary>
        /// <param name="cancellationToken">未使用。</param>
        /// <returns>已完成的工作。</returns>
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
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
        /// 記錄送出的訊息並觸發 <see cref="LineSent"/>。
        /// </summary>
        /// <param name="line">送出的訊息。</param>
        /// <param name="cancellationToken">未使用。</param>
        /// <returns>已完成的工作。</returns>
        public Task SendLineAsync(string line, CancellationToken cancellationToken = default)
        {
            SentLines.Add(line);
            LineSent?.Invoke(line);
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
        /// 假連線沒有需要釋放的資源。
        /// </summary>
        /// <returns>已完成的工作。</returns>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
