/*
 * 檔案：MachineTrigger.cs
 * 專案：Controller.Core
 * 功能：定義會讓機台狀態改變的觸發條件：操作員的指令（啟動、暫停、停止、復歸），
 *       以及機台主動發出的警報。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3 機台狀態機）
 */

namespace Controller.Core.Machine;

/// <summary>
/// 狀態轉換的觸發條件。
/// </summary>
public enum MachineTrigger
{
    /// <summary>啟動（待機或暫停 → 運轉）。對應指令 START。</summary>
    Start,

    /// <summary>暫停（運轉 → 暫停）。對應指令 PAUSE。</summary>
    Pause,

    /// <summary>停止（運轉或暫停 → 待機）。對應指令 STOP。</summary>
    Stop,

    /// <summary>警報（待機、運轉或暫停 → 警報）。由機台主動事件 EVT ALARM 觸發，操作員不能下達。</summary>
    Alarm,

    /// <summary>復歸（警報 → 待機）。對應指令 RESET。</summary>
    Reset,
}
