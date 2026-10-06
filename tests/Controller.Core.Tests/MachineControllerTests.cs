/*
 * 檔案：MachineControllerTests.cs
 * 專案：Controller.Core.Tests
 * 功能：MachineController（機台控制器）的單元測試。用假指令傳送者模擬機台回應，驗證：
 *       合法操作會送指令並轉換狀態、非法操作被拒絕且不送指令、機台回錯誤或逾時時狀態不變、
 *       警報事件切換到警報狀態、與機台同步狀態、狀態回應的解析。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3 機台狀態機）
 */

using Controller.Core.Machine;
using Controller.Core.Tests.Fakes;

namespace Controller.Core.Tests;

/// <summary>
/// <see cref="MachineController"/> 的單元測試。
/// </summary>
public class MachineControllerTests
{
    /// <summary>
    /// 待機時啟動：應送出 START，機台回 OK 後狀態變為運轉。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task Execute_StartWhenIdle_SendsCommandAndMovesToRunning()
    {
        var sender = new FakeCommandSender(_ => "OK RUNNING");
        var controller = new MachineController(sender);

        bool ok = await controller.ExecuteAsync(MachineTrigger.Start);

        Assert.True(ok);
        Assert.Equal(MachineState.Running, controller.StateMachine.State);
        Assert.Equal<string>(new[] { "START" }, sender.SentCommands);
    }

    /// <summary>
    /// 待機時暫停（非法）：應被拒絕並記錄，而且完全不送指令給機台。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task Execute_PauseWhenIdle_IsRejectedWithoutSending()
    {
        var sender = new FakeCommandSender(_ => "OK");
        var controller = new MachineController(sender);
        var logs = new List<string>();
        controller.Log += logs.Add;

        bool ok = await controller.ExecuteAsync(MachineTrigger.Pause);

        Assert.False(ok);
        Assert.Equal(MachineState.Idle, controller.StateMachine.State);
        Assert.Empty(sender.SentCommands);
        Assert.Contains(logs, log => log.Contains("拒絕"));
    }

    /// <summary>
    /// 機台回錯誤（ERR）：狀態不變，並記錄機台拒絕的原因。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task Execute_DeviceRepliesError_StateUnchanged()
    {
        var sender = new FakeCommandSender(_ => "ERR INVALID_STATE ALARM");
        var controller = new MachineController(sender);
        var logs = new List<string>();
        controller.Log += logs.Add;

        bool ok = await controller.ExecuteAsync(MachineTrigger.Start);

        Assert.False(ok);
        Assert.Equal(MachineState.Idle, controller.StateMachine.State);
        Assert.Contains(logs, log => log.Contains("ERR INVALID_STATE"));
    }

    /// <summary>
    /// 指令逾時：不拋出例外，回傳 false、狀態不變並記錄失敗。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task Execute_Timeout_StateUnchangedAndNoException()
    {
        var sender = new FakeCommandSender(_ => throw new TimeoutException("指令逾時"));
        var controller = new MachineController(sender);
        var logs = new List<string>();
        controller.Log += logs.Add;

        bool ok = await controller.ExecuteAsync(MachineTrigger.Start);

        Assert.False(ok);
        Assert.Equal(MachineState.Idle, controller.StateMachine.State);
        Assert.Contains(logs, log => log.Contains("失敗"));
    }

    /// <summary>
    /// 完整流程：啟動 → 暫停 → 繼續 → 停止，每一步都送出正確的指令。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task Execute_FullCycle_SendsExpectedCommands()
    {
        var sender = new FakeCommandSender(_ => "OK");
        var controller = new MachineController(sender);

        Assert.True(await controller.ExecuteAsync(MachineTrigger.Start));
        Assert.True(await controller.ExecuteAsync(MachineTrigger.Pause));
        Assert.True(await controller.ExecuteAsync(MachineTrigger.Start));
        Assert.True(await controller.ExecuteAsync(MachineTrigger.Stop));

        Assert.Equal(MachineState.Idle, controller.StateMachine.State);
        Assert.Equal<string>(new[] { "START", "PAUSE", "START", "STOP" }, sender.SentCommands);
    }

    /// <summary>
    /// 警報不能由操作員下達，應拋出 ArgumentException。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task Execute_AlarmTrigger_Throws()
    {
        var controller = new MachineController(new FakeCommandSender(_ => "OK"));

        await Assert.ThrowsAsync<ArgumentException>(() => controller.ExecuteAsync(MachineTrigger.Alarm));
    }

    /// <summary>
    /// 運轉中收到 EVT ALARM：應切換到警報狀態，並記錄警報代碼。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task HandleEvent_AlarmWhileRunning_MovesToAlarm()
    {
        var controller = new MachineController(new FakeCommandSender(_ => "OK"));
        await controller.ExecuteAsync(MachineTrigger.Start);
        var logs = new List<string>();
        controller.Log += logs.Add;

        bool handled = controller.HandleEvent("EVT ALARM E101");

        Assert.True(handled);
        Assert.Equal(MachineState.Alarm, controller.StateMachine.State);
        Assert.Contains(logs, log => log.Contains("E101"));
    }

    /// <summary>
    /// 已在警報狀態時又收到警報：狀態維持警報，不會出錯。
    /// </summary>
    [Fact]
    public void HandleEvent_AlarmWhileInAlarm_StaysInAlarm()
    {
        var controller = new MachineController(new FakeCommandSender(_ => "OK"));
        controller.HandleEvent("EVT ALARM E101");

        bool handled = controller.HandleEvent("EVT ALARM E102");

        Assert.True(handled);
        Assert.Equal(MachineState.Alarm, controller.StateMachine.State);
    }

    /// <summary>
    /// 不是 EVT 開頭的訊息不是機台事件，應回傳 false 交給呼叫端處理。
    /// </summary>
    [Fact]
    public void HandleEvent_NotAnEvent_ReturnsFalse()
    {
        var controller = new MachineController(new FakeCommandSender(_ => "OK"));

        Assert.False(controller.HandleEvent("OK PONG"));
        Assert.Equal(MachineState.Idle, controller.StateMachine.State);
    }

    /// <summary>
    /// 警報後復歸：應送出 RESET，回到待機。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task Execute_ResetAfterAlarm_BackToIdle()
    {
        var sender = new FakeCommandSender(_ => "OK IDLE");
        var controller = new MachineController(sender);
        controller.HandleEvent("EVT ALARM E101");

        bool ok = await controller.ExecuteAsync(MachineTrigger.Reset);

        Assert.True(ok);
        Assert.Equal(MachineState.Idle, controller.StateMachine.State);
        Assert.Equal<string>(new[] { "RESET" }, sender.SentCommands);
    }

    /// <summary>
    /// 同步：機台回 OK PAUSED，狀態機應直接變成暫停。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task SyncAsync_DeviceReportsPaused_StateBecomesPaused()
    {
        var sender = new FakeCommandSender(_ => "OK PAUSED");
        var controller = new MachineController(sender);

        bool ok = await controller.SyncAsync();

        Assert.True(ok);
        Assert.Equal(MachineState.Paused, controller.StateMachine.State);
        Assert.Equal<string>(new[] { "GET STATUS" }, sender.SentCommands);
    }

    /// <summary>
    /// 同步：機台回應無法解析時，狀態不變並記錄。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task SyncAsync_UnknownReply_StateUnchanged()
    {
        var controller = new MachineController(new FakeCommandSender(_ => "OK BANANA"));

        bool ok = await controller.SyncAsync();

        Assert.False(ok);
        Assert.Equal(MachineState.Idle, controller.StateMachine.State);
    }

    /// <summary>
    /// 狀態回應解析：只接受「OK 狀態名稱」，大小寫不拘；錯誤格式與數字都不接受。
    /// </summary>
    [Fact]
    public void TryParseStatus_AcceptsOnlyValidStatus()
    {
        Assert.True(MachineController.TryParseStatus("OK RUNNING", out MachineState running));
        Assert.Equal(MachineState.Running, running);
        Assert.True(MachineController.TryParseStatus("ok alarm", out MachineState alarm));
        Assert.Equal(MachineState.Alarm, alarm);

        Assert.False(MachineController.TryParseStatus("OK PONG", out _));
        Assert.False(MachineController.TryParseStatus("OK 1", out _));
        Assert.False(MachineController.TryParseStatus("ERR INVALID_STATE IDLE", out _));
        Assert.False(MachineController.TryParseStatus("OK", out _));
    }
}
