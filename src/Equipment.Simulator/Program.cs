/*
 * 檔案：Program.cs
 * 專案：Equipment.Simulator
 * 功能：模擬機台（TCP Server）。在 127.0.0.1:5000 監聽，以「一行一筆，\n 結尾」的文字協定回應指令：
 *       - GET STATUS：回覆 OK IDLE，並故意分兩段送出，用來驗證主控端的封包切割
 *       - PING      ：回覆 OK PONG，供主控端心跳使用
 *       - SLEEP     ：故意不回應，用來驗證主控端的指令逾時
 *       - HANG      ：回覆 OK HANG 後模擬設備當機（連線還在但之後完全不回應），
 *                     用來驗證主控端靠心跳偵測斷線並自動重連
 *       - 其他指令  ：回覆 ERR UNKNOWN_COMMAND
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.3（Slice 2 斷線重連）
 */

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Equipment.Simulator;

/// <summary>
/// 模擬機台的進入點、連線處理與指令處理。
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
        Log("支援指令：GET STATUS、PING、SLEEP（不回應）、HANG（模擬當機）");

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
    /// 處理單一主控程式的連線：一行一行讀取指令並回應，直到對方離線或程式結束。
    /// </summary>
    /// <param name="client">已接受的 TCP 連線。</param>
    /// <param name="ct">程式結束時用來中止讀取的取消權杖。</param>
    /// <returns>代表此連線處理流程的非同步工作。</returns>
    private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint;
        Log($"主控已連上：{endpoint}");

        // 這條連線是否處於「當機」狀態；每條連線各自獨立，重連後的新連線會恢復正常
        bool hung = false;

        try
        {
            using (client)
            {
                // 關掉 Nagle 演算法，讓分段送出真的變成兩個 TCP 封包
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();

                // 模擬機台這端用 StreamReader 讀行就好；主控端則自己實作 LineFramer 練習封包切割
                using var reader = new StreamReader(stream, Encoding.UTF8);

                while (true)
                {
                    string? line = await reader.ReadLineAsync(ct);

                    // 讀到 null 代表對方正常關閉連線
                    if (line is null) break;

                    line = line.Trim();
                    if (line.Length == 0) continue;

                    if (hung)
                    {
                        // 當機中：照樣收資料（連線不斷），但完全不回應
                        Log($"（當機中，不回應）收到：{line}");
                        continue;
                    }

                    if (line.Equals("HANG", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendRawAsync(stream, "OK HANG\n", ct);
                        hung = true;
                        Log("收到 HANG：模擬設備當機，之後這條連線不再回應任何指令");
                        continue;
                    }

                    await HandleCommandAsync(stream, line, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 程式結束時的取消，屬於正常流程
        }
        catch (IOException ex)
        {
            // 例如主控程式被強制關閉，或主控端判定斷線後主動關閉
            Log($"連線異常：{ex.Message}");
        }

        Log($"主控已離線：{endpoint}");
    }

    /// <summary>
    /// 依指令內容回應主控程式（HANG 由 <see cref="HandleClientAsync"/> 處理）。
    /// </summary>
    /// <param name="stream">要回寫的網路串流。</param>
    /// <param name="command">收到的指令（已去除前後空白）。</param>
    /// <param name="ct">程式結束時用來中止回寫的取消權杖。</param>
    /// <returns>代表回應動作的非同步工作。</returns>
    private static async Task HandleCommandAsync(NetworkStream stream, string command, CancellationToken ct)
    {
        switch (command.ToUpperInvariant())
        {
            case "GET STATUS":
                Log($"收到指令：{command}");
                // 故意把 "OK IDLE\n" 拆成兩段送，模擬真實設備的半包
                await SendRawAsync(stream, "OK ", ct);
                await Task.Delay(300, ct);
                await SendRawAsync(stream, "IDLE\n", ct);
                Log("回覆：OK IDLE（分兩段送出）");
                break;

            case "PING":
                // 心跳每 5 秒一次，只印一行簡短紀錄，避免洗版
                await SendRawAsync(stream, "OK PONG\n", ct);
                Log("心跳：PING → OK PONG");
                break;

            case "SLEEP":
                Log($"收到指令：{command}，故意不回應，用來測試主控的逾時");
                break;

            default:
                Log($"收到指令：{command}");
                await SendRawAsync(stream, "ERR UNKNOWN_COMMAND\n", ct);
                Log("回覆：ERR UNKNOWN_COMMAND");
                break;
        }
    }

    /// <summary>
    /// 把文字以 UTF-8 原樣寫入串流（不自動補 \n，方便故意分段送出）。
    /// </summary>
    /// <param name="stream">要寫入的網路串流。</param>
    /// <param name="text">要送出的文字。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>代表寫入動作的非同步工作。</returns>
    private static Task SendRawAsync(NetworkStream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();

    /// <summary>
    /// 在主控台印出帶有時間戳記（到毫秒）的訊息。
    /// </summary>
    /// <param name="message">要印出的訊息內容。</param>
    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
}
