/*
 * 檔案：ConnectionState.cs
 * 專案：Controller.Core
 * 功能：定義連線監控器（ConnectionSupervisor）的連線狀態。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.3（Slice 2 斷線重連）
 */

namespace Controller.Core.Connection;

/// <summary>
/// 連線狀態。
/// </summary>
public enum ConnectionState
{
    /// <summary>未連線（尚未啟動，或已手動中斷）。</summary>
    Disconnected,

    /// <summary>第一次連線中。</summary>
    Connecting,

    /// <summary>已連線，心跳正常監控中。</summary>
    Connected,

    /// <summary>連線中斷後，正在自動重連。</summary>
    Reconnecting,
}
