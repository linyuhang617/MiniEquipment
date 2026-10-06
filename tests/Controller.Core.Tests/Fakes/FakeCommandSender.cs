/*
 * 檔案：FakeCommandSender.cs
 * 專案：Controller.Core.Tests
 * 功能：測試用的假指令傳送者。由測試程式提供「收到什麼指令就回什麼」的函式，
 *       也可以拋出例外模擬逾時或斷線，並記錄所有送出過的指令。
 *
 * @author  linyuhang617
 * @since   2026-10-07
 * @version 0.4（Slice 3 機台狀態機）
 */

using Controller.Core.Protocol;

namespace Controller.Core.Tests.Fakes;

/// <summary>
/// 實作 <see cref="ICommandSender"/> 的假指令傳送者。
/// </summary>
internal sealed class FakeCommandSender : ICommandSender
{
    /// <summary>依指令決定回應的函式；拋出例外可模擬逾時或斷線。</summary>
    private readonly Func<string, string> _respond;

    /// <summary>所有送出過的指令。</summary>
    private readonly List<string> _sentCommands = new();

    /// <summary>
    /// 建立假指令傳送者。
    /// </summary>
    /// <param name="respond">依指令決定回應的函式，例如 <c>cmd =&gt; "OK RUNNING"</c>。</param>
    public FakeCommandSender(Func<string, string> respond) => _respond = respond;

    /// <summary>
    /// 取得所有送出過的指令，依送出順序排列。
    /// </summary>
    public IReadOnlyList<string> SentCommands => _sentCommands;

    /// <summary>
    /// 記錄指令並回傳預設的回應；回應函式拋出例外時，回傳失敗的工作。
    /// </summary>
    /// <param name="command">送出的指令。</param>
    /// <param name="timeout">未使用。</param>
    /// <param name="cancellationToken">未使用。</param>
    /// <returns>預設的回應。</returns>
    public Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _sentCommands.Add(command);

        try
        {
            return Task.FromResult(_respond(command));
        }
        catch (Exception ex)
        {
            return Task.FromException<string>(ex);
        }
    }
}
