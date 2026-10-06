/*
 * 檔案：Program.cs
 * 專案：Equipment.Simulator
 * 功能：模擬機台（TCP Server）。在 127.0.0.1:5000 監聽，
 *       主控程式連上或離線時在主控台印出訊息，並印出收到的資料。
 *       Slice 0 只負責「能被連上」，還不處理任何指令。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.1（Slice 0 Walking Skeleton）
 */

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Equipment.Simulator;

/// <summary>
/// 模擬機台的進入點與連線處理。
/// </summary>
internal static class Program
{
    /// <summary>監聽的 TCP port，需與主控程式的設定一致。</summary>
    private const int Port = 5000;

    /// <summary>
    /// 程式進入點：啟動 TCP 監聽，持續接受主控程式連線，直到按下 Ctrl+C。
    /// </summary>
    /// <returns>代表整個監聽流程的非同步工作。</returns>
    private static async Task Main()
    {
        // 讓主控台能正確顯示中文
        Console.OutputEncoding = Encoding.UTF8;

        // 只聽本機（Loopback），避免 Windows 防火牆跳出詢問視窗
        var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start();
        Log($"模擬機台啟動，監聽 127.0.0.1:{Port}，按 Ctrl+C 結束。");

        // 用 CancellationTokenSource 統一通知所有非同步工作「該結束了」
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // 不要直接砍掉程序，讓程式自己收尾
            cts.Cancel();
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                // 等待下一個主控程式連線（await 期間不佔用執行緒）
                TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);

                // 每個連線各自在背景處理，不擋住下一個 Accept
                _ = HandleClientAsync(client, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 按下 Ctrl+C 造成的取消，屬於正常結束
        }
        finally
        {
            listener.Stop();
            Log("模擬機台已關閉。");
        }
    }

    /// <summary>
    /// 處理單一主控程式的連線：持續讀取資料並印出，直到對方離線或程式結束。
    /// </summary>
    /// <param name="client">已接受的 TCP 連線。</param>
    /// <param name="ct">程式結束時用來中止讀取的取消權杖。</param>
    /// <returns>代表此連線處理流程的非同步工作。</returns>
    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint;
        Log($"主控已連上：{endpoint}");

        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                var buffer = new byte[1024];

                while (true)
                {
                    int n = await stream.ReadAsync(buffer, ct);

                    // 讀到 0 byte 代表對方正常關閉連線
                    if (n == 0) break;

                    // Slice 0 只把收到的資料印出來，Slice 1 才處理指令
                    Log($"收到 {n} bytes：{Encoding.UTF8.GetString(buffer, 0, n).TrimEnd()}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 程式結束時的取消，屬於正常流程
        }
        catch (IOException ex)
        {
            // 例如主控程式被強制關閉，連線被重置
            Log($"連線異常：{ex.Message}");
        }

        Log($"主控已離線：{endpoint}");
    }

    /// <summary>
    /// 在主控台印出帶有時間戳記（到毫秒）的訊息。
    /// </summary>
    /// <param name="message">要印出的訊息內容。</param>
    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
