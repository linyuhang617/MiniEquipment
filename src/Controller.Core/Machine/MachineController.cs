/*
 * 檔案：MachineController.cs
 * 專案：Controller.Core
 * 功能：機台控制器。把狀態機與指令收發串起來：
 *       1. 操作員按「啟動、暫停、停止、復歸」時，先用狀態機檢查是否合法，
 *          非法就拒絕並記錄、不送給機台；合法才送指令，機台回 OK 才真的轉換狀態。
 *       2. 收到機台主動事件 EVT ALARM &lt;代碼&gt; 時，切換到警報狀態。
 *       3. 連線或重連後用 GET STATUS 與機台同步狀態（斷線期間機台可能已經改變）。
 *       所有結果透過 Log 事件回報，由畫面顯示。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3 機台狀態機）
 */

using Controller.Core.Protocol;

namespace Controller.Core.Machine;

/// <summary>
/// 依狀態機規則對機台下指令，並處理警報事件與狀態同步。
/// </summary>
public sealed class MachineController
{
    /// <summary>操作員觸發條件對應的機台指令。警報不在表上，因為只能由機台觸發。</summary>
    private static readonly IReadOnlyDictionary<MachineTrigger, string> CommandTexts =
        new Dictionary<MachineTrigger, string>
        {
            [MachineTrigger.Start] = "START",
            [MachineTrigger.Pause] = "PAUSE",
            [MachineTrigger.Stop] = "STOP",
            [MachineTrigger.Reset] = "RESET",
        };

    /// <summary>送出指令用的介面（實際上是 ConnectionSupervisor）。</summary>
    private readonly ICommandSender _sender;

    /// <summary>指令等待回應的逾時時間。</summary>
    private readonly TimeSpan _commandTimeout;

    /// <summary>
    /// 建立機台控制器，初始狀態為待機（連線後會與機台同步）。
    /// </summary>
    /// <param name="sender">送出指令用的介面。</param>
    /// <param name="commandTimeout">指令逾時時間；未指定時預設 3 秒。</param>
    /// <exception cref="ArgumentNullException">sender 為 null。</exception>
    public MachineController(ICommandSender sender, TimeSpan? commandTimeout = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(3);
        StateMachine = new MachineStateMachine();
    }

    /// <summary>
    /// 取得機台狀態機。
    /// </summary>
    public MachineStateMachine StateMachine { get; }

    /// <summary>
    /// 有值得記錄的事情時觸發（拒絕、失敗、警報、同步）。可能在背景執行緒觸發。
    /// </summary>
    public event Action<string>? Log;

    /// <summary>
    /// 執行操作員指令：檢查合法 → 送指令 → 機台回 OK 才轉換狀態。
    /// </summary>
    /// <param name="trigger">操作員的觸發條件（啟動、暫停、停止、復歸）。</param>
    /// <param name="cancellationToken">用來取消等待的權杖。</param>
    /// <returns>狀態成功轉換時為 true；被拒絕、機台回錯誤或通訊失敗時為 false。</returns>
    /// <exception cref="ArgumentException">trigger 是警報（操作員不能下達）。</exception>
    /// <remarks>
    /// 通訊失敗不會拋出例外，而是記錄後回傳 false，狀態維持不變，呼叫端不必處理例外。
    /// </remarks>
    public async Task<bool> ExecuteAsync(MachineTrigger trigger, CancellationToken cancellationToken = default)
    {
        if (!CommandTexts.TryGetValue(trigger, out string? command))
            throw new ArgumentException($"「{Describe(trigger)}」只能由機台觸發，操作員不能下達。", nameof(trigger));

        // 第一道防線：狀態機檢查。非法轉換直接拒絕，不送給機台
        MachineState before = StateMachine.State;
        if (!StateMachine.CanFire(trigger))
        {
            WriteLog($"拒絕：{Describe(before)}狀態不能「{Describe(trigger)}」");
            return false;
        }

        string reply;
        try
        {
            reply = await _sender.SendCommandAsync(command, _commandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 逾時、斷線等：狀態維持不變（機台到底有沒有收到不確定，下次連線會同步）
            WriteLog($"「{Describe(trigger)}」失敗：{ex.Message}，狀態維持{Describe(StateMachine.State)}");
            return false;
        }

        // 第二道防線：機台自己也會檢查，回 ERR 就不轉換
        if (!reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            WriteLog($"機台拒絕「{Describe(trigger)}」：{reply}");
            return false;
        }

        // 等回應的期間可能剛好收到警報，狀態已經變了，就不再套用這次的轉換
        if (!StateMachine.TryFire(trigger, $"操作員{Describe(trigger)}"))
        {
            WriteLog($"機台已回應 {reply}，但狀態已變為{Describe(StateMachine.State)}，不套用「{Describe(trigger)}」");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 處理機台主動送來的事件。目前支援 EVT ALARM &lt;代碼&gt;。
    /// </summary>
    /// <param name="line">收到的訊息。</param>
    /// <returns>是機台事件（已處理）時為 true；不是事件時為 false，呼叫端可另行處理。</returns>
    public bool HandleEvent(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("EVT", StringComparison.OrdinalIgnoreCase))
            return false;

        if (parts[1].Equals("ALARM", StringComparison.OrdinalIgnoreCase))
        {
            string code = parts.Length >= 3 ? parts[2] : "UNKNOWN";

            if (StateMachine.TryFire(MachineTrigger.Alarm, $"機台警報 {code}"))
                WriteLog($"警報 {code}：機台已停止，請排除問題後按「復歸」");
            else
                WriteLog($"收到警報 {code}，目前已是{Describe(StateMachine.State)}狀態");

            return true;
        }

        WriteLog($"未支援的機台事件：{line}");
        return true;
    }

    /// <summary>
    /// 用 GET STATUS 查詢機台實際狀態並同步到狀態機。
    /// </summary>
    /// <param name="cancellationToken">用來取消等待的權杖。</param>
    /// <returns>同步成功為 true；通訊失敗或回應無法解析為 false。</returns>
    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        string reply;
        try
        {
            reply = await _sender.SendCommandAsync("GET STATUS", _commandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteLog($"查詢機台狀態失敗：{ex.Message}");
            return false;
        }

        if (!TryParseStatus(reply, out MachineState actual))
        {
            WriteLog($"無法解析機台狀態：{reply}");
            return false;
        }

        StateMachine.Synchronize(actual, "與機台同步");
        WriteLog($"機台目前狀態：{Describe(actual)}");
        return true;
    }

    /// <summary>
    /// 解析機台的狀態回應，例如 "OK RUNNING" → <see cref="MachineState.Running"/>。
    /// </summary>
    /// <param name="reply">機台的回應。</param>
    /// <param name="state">解析成功時為機台狀態。</param>
    /// <returns>解析成功時為 true。</returns>
    public static bool TryParseStatus(string reply, out MachineState state)
    {
        state = default;
        string[] parts = reply.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // 只接受「OK 狀態名稱」；排除數字，避免 "OK 1" 被 Enum.TryParse 當成狀態
        return parts.Length == 2
            && parts[0].Equals("OK", StringComparison.OrdinalIgnoreCase)
            && !int.TryParse(parts[1], out _)
            && Enum.TryParse(parts[1], ignoreCase: true, out state)
            && Enum.IsDefined(state);
    }

    /// <summary>
    /// 取得狀態的中文名稱。
    /// </summary>
    /// <param name="state">機台狀態。</param>
    /// <returns>中文名稱，例如「運轉」。</returns>
    public static string Describe(MachineState state) => state switch
    {
        MachineState.Idle => "待機",
        MachineState.Running => "運轉",
        MachineState.Paused => "暫停",
        MachineState.Alarm => "警報",
        _ => state.ToString(),
    };

    /// <summary>
    /// 取得觸發條件的中文名稱。
    /// </summary>
    /// <param name="trigger">觸發條件。</param>
    /// <returns>中文名稱，例如「復歸」。</returns>
    public static string Describe(MachineTrigger trigger) => trigger switch
    {
        MachineTrigger.Start => "啟動",
        MachineTrigger.Pause => "暫停",
        MachineTrigger.Stop => "停止",
        MachineTrigger.Alarm => "警報",
        MachineTrigger.Reset => "復歸",
        _ => trigger.ToString(),
    };

    /// <summary>
    /// 觸發 <see cref="Log"/> 事件。
    /// </summary>
    /// <param name="message">要記錄的訊息。</param>
    private void WriteLog(string message) => Log?.Invoke(message);
}
