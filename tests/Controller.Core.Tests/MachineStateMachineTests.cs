/*
 * 檔案：MachineStateMachineTests.cs
 * 專案：Controller.Core.Tests
 * 功能：MachineStateMachine（機台狀態機）的單元測試。
 *       用 xUnit 的 [Theory] + [InlineData]，一行資料就是一個測試案例：
 *       9 個合法轉換各一個案例、11 個非法轉換各一個案例，
 *       另外驗證初始狀態、事件內容、允許的操作清單與狀態同步。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3 機台狀態機）
 */

using Controller.Core.Machine;

namespace Controller.Core.Tests;

/// <summary>
/// <see cref="MachineStateMachine"/> 的單元測試。
/// </summary>
public class MachineStateMachineTests
{
    /// <summary>
    /// 新建立的狀態機應為待機。
    /// </summary>
    [Fact]
    public void NewStateMachine_IsIdle()
    {
        var machine = new MachineStateMachine();

        Assert.Equal(MachineState.Idle, machine.State);
    }

    /// <summary>
    /// 合法轉換：每一行對應狀態圖上的一條箭頭，執行後應到達預期狀態。
    /// </summary>
    /// <param name="from">起始狀態。</param>
    /// <param name="trigger">觸發條件。</param>
    /// <param name="expected">預期的新狀態。</param>
    [Theory]
    [InlineData(MachineState.Idle, MachineTrigger.Start, MachineState.Running)]
    [InlineData(MachineState.Running, MachineTrigger.Pause, MachineState.Paused)]
    [InlineData(MachineState.Paused, MachineTrigger.Start, MachineState.Running)]
    [InlineData(MachineState.Running, MachineTrigger.Stop, MachineState.Idle)]
    [InlineData(MachineState.Paused, MachineTrigger.Stop, MachineState.Idle)]
    [InlineData(MachineState.Idle, MachineTrigger.Alarm, MachineState.Alarm)]
    [InlineData(MachineState.Running, MachineTrigger.Alarm, MachineState.Alarm)]
    [InlineData(MachineState.Paused, MachineTrigger.Alarm, MachineState.Alarm)]
    [InlineData(MachineState.Alarm, MachineTrigger.Reset, MachineState.Idle)]
    public void TryFire_ValidTransition_MovesToExpectedState(
        MachineState from, MachineTrigger trigger, MachineState expected)
    {
        var machine = new MachineStateMachine(from);

        bool fired = machine.TryFire(trigger, "測試");

        Assert.True(fired);
        Assert.Equal(expected, machine.State);
    }

    /// <summary>
    /// 非法轉換：應回傳 false、狀態不變、不觸發事件。
    /// </summary>
    /// <param name="from">起始狀態。</param>
    /// <param name="trigger">不允許的觸發條件。</param>
    [Theory]
    [InlineData(MachineState.Idle, MachineTrigger.Pause)]
    [InlineData(MachineState.Idle, MachineTrigger.Stop)]
    [InlineData(MachineState.Idle, MachineTrigger.Reset)]
    [InlineData(MachineState.Running, MachineTrigger.Start)]
    [InlineData(MachineState.Running, MachineTrigger.Reset)]
    [InlineData(MachineState.Paused, MachineTrigger.Pause)]
    [InlineData(MachineState.Paused, MachineTrigger.Reset)]
    [InlineData(MachineState.Alarm, MachineTrigger.Start)]
    [InlineData(MachineState.Alarm, MachineTrigger.Pause)]
    [InlineData(MachineState.Alarm, MachineTrigger.Stop)]
    [InlineData(MachineState.Alarm, MachineTrigger.Alarm)]
    public void TryFire_InvalidTransition_IsRejectedAndStateUnchanged(MachineState from, MachineTrigger trigger)
    {
        var machine = new MachineStateMachine(from);
        int eventCount = 0;
        machine.StateChanged += (_, _, _) => eventCount++;

        bool fired = machine.TryFire(trigger, "測試");

        Assert.False(fired);
        Assert.Equal(from, machine.State);
        Assert.Equal(0, eventCount);
    }

    /// <summary>
    /// 合法轉換應觸發一次 StateChanged，並帶出原狀態、新狀態與原因。
    /// </summary>
    [Fact]
    public void TryFire_Valid_RaisesStateChangedWithDetails()
    {
        var machine = new MachineStateMachine();
        var events = new List<(MachineState From, MachineState To, string Reason)>();
        machine.StateChanged += (from, to, reason) => events.Add((from, to, reason));

        machine.TryFire(MachineTrigger.Start, "操作員啟動");

        var single = Assert.Single(events);
        Assert.Equal(MachineState.Idle, single.From);
        Assert.Equal(MachineState.Running, single.To);
        Assert.Equal("操作員啟動", single.Reason);
    }

    /// <summary>
    /// 每個狀態允許的操作清單應與狀態圖一致（畫面依此決定按鈕能不能按）。
    /// </summary>
    [Fact]
    public void GetPermittedTriggers_MatchesStateDiagram()
    {
        Assert.Equal<MachineTrigger>(
            new[] { MachineTrigger.Start, MachineTrigger.Alarm },
            new MachineStateMachine(MachineState.Idle).GetPermittedTriggers());
        Assert.Equal<MachineTrigger>(
            new[] { MachineTrigger.Pause, MachineTrigger.Stop, MachineTrigger.Alarm },
            new MachineStateMachine(MachineState.Running).GetPermittedTriggers());
        Assert.Equal<MachineTrigger>(
            new[] { MachineTrigger.Start, MachineTrigger.Stop, MachineTrigger.Alarm },
            new MachineStateMachine(MachineState.Paused).GetPermittedTriggers());
        Assert.Equal<MachineTrigger>(
            new[] { MachineTrigger.Reset },
            new MachineStateMachine(MachineState.Alarm).GetPermittedTriggers());
    }

    /// <summary>
    /// 同步到不同的狀態時，應直接改變狀態（不檢查轉換規則）並觸發事件。
    /// </summary>
    [Fact]
    public void Synchronize_DifferentState_ChangesStateAndRaisesEvent()
    {
        var machine = new MachineStateMachine();
        int eventCount = 0;
        machine.StateChanged += (_, _, _) => eventCount++;

        // 待機 → 暫停 不是合法轉換，但同步以機台為準，所以允許
        machine.Synchronize(MachineState.Paused, "與機台同步");

        Assert.Equal(MachineState.Paused, machine.State);
        Assert.Equal(1, eventCount);
    }

    /// <summary>
    /// 同步到相同的狀態時，不應觸發事件。
    /// </summary>
    [Fact]
    public void Synchronize_SameState_NoEvent()
    {
        var machine = new MachineStateMachine();
        int eventCount = 0;
        machine.StateChanged += (_, _, _) => eventCount++;

        machine.Synchronize(MachineState.Idle, "與機台同步");

        Assert.Equal(0, eventCount);
    }
}
