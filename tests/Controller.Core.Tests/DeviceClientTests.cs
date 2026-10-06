/*
 * 檔案：DeviceClientTests.cs
 * 專案：Controller.Core.Tests
 * 功能：DeviceClient（請求／回應）的單元測試。用假的連線（FakeDeviceConnection）
 *       取代真正的網路，精準控制「設備什麼時候回什麼」，驗證正常回應、逾時、
 *       逾時後的遲到回應、等待中斷線、非預期訊息等情況。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.3（Slice 2：假連線移到 Fakes/FakeDeviceConnection.cs 共用）
 */

using Controller.Core.Protocol;
using Controller.Core.Tests.Fakes;

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
}
