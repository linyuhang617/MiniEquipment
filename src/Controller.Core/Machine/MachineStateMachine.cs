/*
 * 檔案：MachineStateMachine.cs
 * 專案：Controller.Core
 * 功能：機台狀態機。用一張「轉換表」定義所有合法的狀態轉換，
 *       不在表上的轉換一律拒絕（例如待機時直接暫停）。
 *       這個類別只管狀態規則，不碰網路與畫面，所以每個轉換都能單獨寫單元測試。
 *       轉換圖見 docs/state-machine.md。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3 機台狀態機）
 */

namespace Controller.Core.Machine;

/// <summary>
/// 以轉換表實作的機台狀態機（執行緒安全）。
/// </summary>
/// <remarks>
/// 用一張表集中定義規則，而不是在各處寫 if/else：
/// 新增或修改規則只要改表，一眼就能和狀態圖對照。
/// </remarks>
public sealed class MachineStateMachine
{
    /// <summary>
    /// 合法轉換表：(目前狀態, 觸發條件) → 下一個狀態。不在表上的組合都是非法轉換。
    /// </summary>
    private static readonly IReadOnlyDictionary<(MachineState From, MachineTrigger Trigger), MachineState> Transitions =
        new Dictionary<(MachineState, MachineTrigger), MachineState>
        {
            [(MachineState.Idle, MachineTrigger.Start)] = MachineState.Running,
            [(MachineState.Running, MachineTrigger.Pause)] = MachineState.Paused,
            [(MachineState.Paused, MachineTrigger.Start)] = MachineState.Running,
            [(MachineState.Running, MachineTrigger.Stop)] = MachineState.Idle,
            [(MachineState.Paused, MachineTrigger.Stop)] = MachineState.Idle,
            [(MachineState.Idle, MachineTrigger.Alarm)] = MachineState.Alarm,
            [(MachineState.Running, MachineTrigger.Alarm)] = MachineState.Alarm,
            [(MachineState.Paused, MachineTrigger.Alarm)] = MachineState.Alarm,
            [(MachineState.Alarm, MachineTrigger.Reset)] = MachineState.Idle,
        };

    /// <summary>保護 <see cref="_state"/> 的鎖物件（警報事件可能來自背景執行緒）。</summary>
    private readonly object _gate = new();

    /// <summary>目前狀態。</summary>
    private MachineState _state;

    /// <summary>
    /// 建立狀態機。
    /// </summary>
    /// <param name="initialState">初始狀態，預設為待機。</param>
    public MachineStateMachine(MachineState initialState = MachineState.Idle)
    {
        _state = initialState;
    }

    /// <summary>
    /// 取得目前狀態。
    /// </summary>
    public MachineState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>
    /// 狀態改變時觸發，參數為（原狀態, 新狀態, 原因）。
    /// </summary>
    /// <remarks>
    /// 可能在背景執行緒觸發；在鎖外觸發，避免訂閱者在事件中又操作狀態機而死結。
    /// </remarks>
    public event Action<MachineState, MachineState, string>? StateChanged;

    /// <summary>
    /// 查詢轉換表：從某個狀態收到某個觸發條件後會到哪個狀態。
    /// </summary>
    /// <param name="from">目前狀態。</param>
    /// <param name="trigger">觸發條件。</param>
    /// <param name="next">合法時為下一個狀態。</param>
    /// <returns>合法轉換時為 true。</returns>
    public static bool TryGetNextState(MachineState from, MachineTrigger trigger, out MachineState next) =>
        Transitions.TryGetValue((from, trigger), out next);

    /// <summary>
    /// 目前狀態下，這個觸發條件是否合法。
    /// </summary>
    /// <param name="trigger">觸發條件。</param>
    /// <returns>合法時為 true。</returns>
    public bool CanFire(MachineTrigger trigger)
    {
        lock (_gate) return Transitions.ContainsKey((_state, trigger));
    }

    /// <summary>
    /// 取得目前狀態下所有合法的觸發條件，畫面用來決定哪些按鈕可以按。
    /// </summary>
    /// <returns>合法的觸發條件清單。</returns>
    public IReadOnlyList<MachineTrigger> GetPermittedTriggers()
    {
        lock (_gate)
        {
            MachineState current = _state;
            return Enum.GetValues<MachineTrigger>()
                .Where(trigger => Transitions.ContainsKey((current, trigger)))
                .ToArray();
        }
    }

    /// <summary>
    /// 嘗試執行一次狀態轉換；非法轉換不改變狀態。
    /// </summary>
    /// <param name="trigger">觸發條件。</param>
    /// <param name="reason">轉換原因，會帶在 <see cref="StateChanged"/> 事件中。</param>
    /// <returns>轉換成功為 true；非法轉換為 false。</returns>
    public bool TryFire(MachineTrigger trigger, string reason)
    {
        MachineState from;
        MachineState to;

        lock (_gate)
        {
            from = _state;
            if (!Transitions.TryGetValue((from, trigger), out to))
                return false;

            _state = to;
        }

        StateChanged?.Invoke(from, to, reason);
        return true;
    }

    /// <summary>
    /// 直接把狀態設成機台回報的實際狀態，不檢查轉換規則。
    /// </summary>
    /// <param name="actual">機台回報的實際狀態。</param>
    /// <param name="reason">同步原因，例如「重連後與機台同步」。</param>
    /// <remarks>
    /// 用在連線或重連之後：斷線期間機台狀態可能已經改變，以機台為準。
    /// 狀態相同時不觸發事件。
    /// </remarks>
    public void Synchronize(MachineState actual, string reason)
    {
        MachineState from;

        lock (_gate)
        {
            from = _state;
            if (from == actual) return;
            _state = actual;
        }

        StateChanged?.Invoke(from, actual, reason);
    }
}
