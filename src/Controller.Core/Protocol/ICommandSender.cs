/*
 * 檔案：ICommandSender.cs
 * 專案：Controller.Core
 * 功能：定義「送出指令並等待回應」的介面。
 *       ConnectionSupervisor 實作它；機台控制器（MachineController）只依賴這個介面，
 *       所以單元測試可以換成假的指令傳送者，不用開網路。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3 機台狀態機）
 */

namespace Controller.Core.Protocol;

/// <summary>
/// 送出指令並等待回應的介面。
/// </summary>
public interface ICommandSender
{
    /// <summary>
    /// 非同步送出一筆指令並等待回應。
    /// </summary>
    /// <param name="command">要送出的指令，例如 "START"。</param>
    /// <param name="timeout">等待回應的最長時間。</param>
    /// <param name="cancellationToken">用來取消等待的權杖。</param>
    /// <returns>設備回應的訊息內容。</returns>
    Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default);
}
