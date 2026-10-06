/*
 * 檔案：MachineState.cs
 * 專案：Controller.Core
 * 功能：定義機台狀態。名稱與模擬機台回覆的狀態文字一致（例如 OK RUNNING）。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3 機台狀態機）
 */

namespace Controller.Core.Machine;

/// <summary>
/// 機台狀態。
/// </summary>
public enum MachineState
{
    /// <summary>待機：可以啟動。</summary>
    Idle,

    /// <summary>運轉中：可以暫停或停止。</summary>
    Running,

    /// <summary>暫停：可以繼續（啟動）或停止。</summary>
    Paused,

    /// <summary>警報：必須先復歸才能再操作。</summary>
    Alarm,
}
