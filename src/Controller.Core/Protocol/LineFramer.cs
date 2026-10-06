/*
 * 檔案：LineFramer.cs
 * 專案：Controller.Core
 * 功能：封包切割。把 TCP 收到的「一段一段」位元組，組回「一行一行」的完整訊息。
 *       TCP 是位元組串流，不保留訊息邊界：
 *       - 半包：一筆訊息可能分好幾次才收完
 *       - 黏包：好幾筆訊息可能一次收到
 *       因此先累積在緩衝區，遇到結尾符號 \n 才切出一筆。
 *       在位元組層級找 \n 再解碼，可避免中文等多位元組字元被切在兩段中間時變成亂碼。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.2（Slice 1 送指令、收回應）
 */

using System.Runtime.InteropServices;
using System.Text;

namespace Controller.Core.Protocol;

/// <summary>
/// 以 \n 為結尾的文字協定封包切割器。
/// </summary>
/// <remarks>
/// 此類別不是執行緒安全的，一條連線應只由一個接收迴圈使用一個實例。
/// </remarks>
public sealed class LineFramer
{
    /// <summary>訊息結尾符號 \n 的位元組值。</summary>
    private const byte LineFeed = (byte)'\n';

    /// <summary>尚未湊成一行的位元組緩衝區。</summary>
    private readonly List<byte> _buffer = new();

    /// <summary>單筆訊息允許的最大長度（bytes），防止緩衝區無限長大。</summary>
    private readonly int _maxLineLength;

    /// <summary>
    /// 建立封包切割器。
    /// </summary>
    /// <param name="maxLineLength">單筆訊息允許的最大長度（bytes），預設 4096。</param>
    /// <exception cref="ArgumentOutOfRangeException">maxLineLength 小於 1 時拋出。</exception>
    public LineFramer(int maxLineLength = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineLength, 1);
        _maxLineLength = maxLineLength;
    }

    /// <summary>
    /// 取得緩衝區裡還沒湊成一行的位元組數。
    /// </summary>
    public int PendingByteCount => _buffer.Count;

    /// <summary>
    /// 放入新收到的資料，回傳這次湊齊的完整訊息。
    /// </summary>
    /// <param name="data">這次從網路收到的位元組。</param>
    /// <returns>湊齊的訊息清單，可能是 0 筆、1 筆或多筆；結尾的 \r\n 或 \n 已去除。</returns>
    /// <exception cref="InvalidDataException">單筆訊息超過最大長度仍未出現 \n。</exception>
    public IReadOnlyList<string> Append(ReadOnlySpan<byte> data)
    {
        var lines = new List<string>();

        foreach (byte b in data)
        {
            if (b == LineFeed)
            {
                // 遇到結尾符號：把緩衝區解碼成一筆訊息，然後清空緩衝區
                string line = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_buffer));
                lines.Add(line.TrimEnd('\r'));   // 同時相容 \r\n 結尾
                _buffer.Clear();
            }
            else
            {
                // 防呆：對方一直不送 \n，不能讓緩衝區無限長大
                if (_buffer.Count >= _maxLineLength)
                    throw new InvalidDataException($"單筆訊息超過 {_maxLineLength} bytes 仍未結束。");

                _buffer.Add(b);
            }
        }

        return lines;
    }
}
