/*
 * 檔案：LineFramerTests.cs
 * 專案：Controller.Core.Tests
 * 功能：LineFramer（封包切割）的單元測試。涵蓋完整一行、半包、黏包、
 *       一筆半、\r\n 結尾、中文字被切開、超長訊息等情況。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.2（Slice 1 送指令、收回應）
 */

using System.Text;
using Controller.Core.Protocol;

namespace Controller.Core.Tests;

/// <summary>
/// <see cref="LineFramer"/> 的單元測試。
/// </summary>
public class LineFramerTests
{
    /// <summary>
    /// 把字串轉成 UTF-8 位元組，模擬從網路收到的資料。
    /// </summary>
    /// <param name="text">要轉換的字串。</param>
    /// <returns>UTF-8 位元組陣列。</returns>
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>
    /// 收到一筆完整訊息，應切出一筆，且緩衝區清空。
    /// </summary>
    [Fact]
    public void Append_CompleteLine_ReturnsOneLine()
    {
        var framer = new LineFramer();

        var lines = framer.Append(Bytes("OK IDLE\n"));

        Assert.Equal<string>(new[] { "OK IDLE" }, lines);
        Assert.Equal(0, framer.PendingByteCount);
    }

    /// <summary>
    /// 半包：一筆訊息分兩次收到，第一次不應切出任何訊息，第二次才切出完整的一筆。
    /// </summary>
    [Fact]
    public void Append_LineSplitInTwo_ReturnsLineOnlyAfterSecondPart()
    {
        var framer = new LineFramer();

        var first = framer.Append(Bytes("OK "));
        var second = framer.Append(Bytes("IDLE\n"));

        Assert.Empty(first);
        Assert.Equal<string>(new[] { "OK IDLE" }, second);
    }

    /// <summary>
    /// 黏包：兩筆訊息一次收到，應切出兩筆。
    /// </summary>
    [Fact]
    public void Append_TwoLinesInOneChunk_ReturnsBoth()
    {
        var framer = new LineFramer();

        var lines = framer.Append(Bytes("OK IDLE\nEVT DOOR_OPEN\n"));

        Assert.Equal<string>(new[] { "OK IDLE", "EVT DOOR_OPEN" }, lines);
    }

    /// <summary>
    /// 一筆半：完整的那筆先切出，剩下的半筆留到下次湊齊。
    /// </summary>
    [Fact]
    public void Append_LineAndHalf_KeepsRemainderForNextAppend()
    {
        var framer = new LineFramer();

        var first = framer.Append(Bytes("OK IDLE\nOK RUN"));
        var second = framer.Append(Bytes("NING\n"));

        Assert.Equal<string>(new[] { "OK IDLE" }, first);
        Assert.Equal<string>(new[] { "OK RUNNING" }, second);
    }

    /// <summary>
    /// \r\n 結尾（Windows 風格）：應去掉 \r，結果與 \n 結尾相同。
    /// </summary>
    [Fact]
    public void Append_CrLfEnding_TrimsCarriageReturn()
    {
        var framer = new LineFramer();

        var lines = framer.Append(Bytes("OK IDLE\r\n"));

        Assert.Equal<string>(new[] { "OK IDLE" }, lines);
    }

    /// <summary>
    /// 中文字被切開：「溫」在 UTF-8 是 3 bytes，從中間切開後仍應正確解碼，不會變亂碼。
    /// </summary>
    [Fact]
    public void Append_MultiByteCharSplitAcrossChunks_DecodesCorrectly()
    {
        var framer = new LineFramer();
        byte[] data = Bytes("溫度 25\n");

        var first = framer.Append(data.AsSpan(0, 2));
        var second = framer.Append(data.AsSpan(2));

        Assert.Empty(first);
        Assert.Equal<string>(new[] { "溫度 25" }, second);
    }

    /// <summary>
    /// 超長訊息：超過最大長度仍沒有 \n，應拋出 InvalidDataException。
    /// </summary>
    [Fact]
    public void Append_LineTooLong_Throws()
    {
        var framer = new LineFramer(maxLineLength: 8);

        Assert.Throws<InvalidDataException>(() => framer.Append(Bytes("123456789")));
    }
}
