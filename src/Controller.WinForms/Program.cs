/*
 * 檔案：Program.cs
 * 專案：Controller.WinForms
 * 功能：WinForms 主控程式的進入點，初始化應用程式設定後開啟主視窗。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.1（Slice 0 Walking Skeleton）
 */

namespace Controller.WinForms;

/// <summary>
/// 應用程式進入點。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 程式進入點：套用高 DPI、預設字型等設定，然後開啟 <see cref="MainForm"/>。
    /// </summary>
    /// <remarks>
    /// [STAThread] 是 WinForms 的必要設定，UI 元件（例如剪貼簿、對話框）需要單執行緒 Apartment。
    /// </remarks>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
