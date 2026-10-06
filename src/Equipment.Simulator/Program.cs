/*
 * 檔案：Program.cs
 * 專案：Equipment.Simulator
 * 功能：模擬機台（TCP Server）。在 127.0.0.1:5000 監聽，以「一行一筆，\n 結尾」的文字協定回應指令。
 *       機台本身也有狀態（IDLE、RUNNING、PAUSED、ALARM），由所有連線共用，斷線重連後狀態不會消失：
 *       - START / PAUSE / STOP / RESET：依目前狀態轉換，成功回 OK &lt;新狀態&gt;，不允許時回 ERR INVALID_STATE &lt;目前狀態&gt;
 *       - GET STATUS：回覆 OK &lt;目前狀態&gt;，並故意分兩段送出，用來驗證主控端的封包切割
 *       - PING      ：回覆 OK PONG，供主控端心跳使用
 *       - SLEEP     ：故意不回應，用來驗證主控端的指令逾時
 *       - HANG      ：回覆 OK HANG 後這條連線不再回應，模擬設備當機
 *       - ALARM     ：測試用，回覆 OK SIMULATED 後觸發警報（效果與按 A 鍵相同）
 *       - 其他指令  ：回覆 ERR UNKNOWN_COMMAND
 *       觸發警報時（ALARM 指令或在主控台按 A 鍵）：機台進入 ALARM，並主動送出 EVT ALARM E101 給所有連線。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.4（Slice 3 機台狀態機）
 */

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Equipment.Simulator;

/// <summary>
/// 模擬機台的進入點、連線處理、指令處理與機台狀態。
/// </summary>
internal static class Program
{
    /// <summary>監聽的 TCP port，需與主控程式的設定一致。</summary>
    private const int Port = 5000;

    /// <summary>保護 <see cref="_machineState"/> 的鎖物件（多條連線與鍵盤都會修改）。</summary>
    private static readonly object StateGate = new();

    /// <summary>所有目前連線中的主控端，用來廣播主動事件。</summary>
    private static readonly ConcurrentDictionary<int, ClientSession> Sessions = new();

    /// <summary>機台目前狀態，所有連線共用。</summary>
    private static string _machineState = "IDLE";

    /// <summary>下一個連線的編號。</summary>
    private static int _nextSessionId;

    /// <summary>
    /// 程式進入點：啟動 TCP 監聽與鍵盤監聽，持續接受主控程式連線，直到按下 Ctrl+C。
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
        Log("支援指令：START、PAUSE、STOP、RESET、GET STATUS、PING、SLEEP（不回應）、HANG（模擬當機）、ALARM（模擬警報）");
        Log($"機台狀態：{GetState()}。按 A 鍵或送出 ALARM 指令可觸發警報。");

        // 用 CancellationTokenSource 統一通知所有非同步工作「該結束了」
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // 不要直接砍掉程序，讓程式自己收尾
            cts.Cancel();
        };

        StartKeyboardLoop(cts.Token);

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
    /// 在背景監聽鍵盤：按 A 鍵觸發警報。輸入被重新導向（例如自動化測試）時不啟用。
    /// </summary>
    /// <param name="ct">程式結束時的取消權杖。</param>
    private static void StartKeyboardLoop(CancellationToken ct)
    {
        if (Console.IsInputRedirected)
        {
            Log("（輸入被重新導向，停用鍵盤快捷鍵）");
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                // ReadKey 會一直等到有按鍵；intercept: true 表示不把按下的字顯示在畫面上
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.A)
                    await TriggerAlarmAsync("E101", ct);
            }
        });
    }

    /// <summary>
    /// 觸發警報：機台進入 ALARM，並主動送出 EVT ALARM &lt;代碼&gt; 給所有連線。
    /// </summary>
    /// <param name="code">警報代碼。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>代表廣播動作的非同步工作。</returns>
    private static async Task TriggerAlarmAsync(string code, CancellationToken ct)
    {
        lock (StateGate) _machineState = "ALARM";
        Log($"觸發警報 {code}，機台狀態：ALARM，通知 {Sessions.Count} 個主控端");

        foreach (ClientSession session in Sessions.Values)
        {
            try
            {
                await SendLineAsync(session, $"EVT ALARM {code}", ct);
            }
            catch (Exception ex)
            {
                Log($"送出警報失敗：{ex.Message}");
            }
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
        int sessionId = Interlocked.Increment(ref _nextSessionId);
        Log($"主控已連上：{endpoint}");

        // 這條連線是否處於「當機」狀態；每條連線各自獨立，重連後的新連線會恢復正常
        bool hung = false;

        try
        {
            using (client)
            {
                // 關掉 Nagle 演算法，讓分段送出真的變成兩個 TCP 封包
                client.NoDelay = true;
                var session = new ClientSession(client.GetStream());
                Sessions[sessionId] = session;

                // 模擬機台這端用 StreamReader 讀行就好；主控端則自己實作 LineFramer 練習封包切割
                using var reader = new StreamReader(session.Stream, Encoding.UTF8);

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
                        await SendLineAsync(session, "OK HANG", ct);
                        hung = true;
                        Log("收到 HANG：模擬設備當機，之後這條連線不再回應任何指令");
                        continue;
                    }

                    await HandleCommandAsync(session, line, ct);
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
        finally
        {
            Sessions.TryRemove(sessionId, out _);
        }

        Log($"主控已離線：{endpoint}");
    }

    /// <summary>
    /// 依指令內容回應主控程式（HANG 由 <see cref="HandleClientAsync"/> 處理）。
    /// </summary>
    /// <param name="session">要回應的連線。</param>
    /// <param name="command">收到的指令（已去除前後空白）。</param>
    /// <param name="ct">程式結束時用來中止回寫的取消權杖。</param>
    /// <returns>代表回應動作的非同步工作。</returns>
    private static async Task HandleCommandAsync(ClientSession session, string command, CancellationToken ct)
    {
        string upper = command.ToUpperInvariant();

        switch (upper)
        {
            case "START":
            case "PAUSE":
            case "STOP":
            case "RESET":
                string reply = TryTransition(upper);
                await SendLineAsync(session, reply, ct);
                Log($"收到指令：{upper} → {reply}");
                break;

            case "GET STATUS":
                // 故意把 "OK <狀態>\n" 拆成兩段送，模擬真實設備的半包
                string state = GetState();
                await SendSplitAsync(session, "OK ", state, ct);
                Log($"收到指令：GET STATUS → OK {state}（分兩段送出）");
                break;

            case "PING":
                // 心跳每 5 秒一次，只印一行簡短紀錄
                await SendLineAsync(session, "OK PONG", ct);
                Log("心跳：PING → OK PONG");
                break;

            case "SLEEP":
                Log($"收到指令：{command}，故意不回應，用來測試主控的逾時");
                break;

            case "ALARM":
                // 測試用：從主控端觸發警報（效果與按 A 鍵相同）。
                // 先回覆這筆指令，再廣播主動事件，主控端才不會把事件誤當成回應
                await SendLineAsync(session, "OK SIMULATED", ct);
                Log("收到指令：ALARM → OK SIMULATED（模擬警報）");
                await TriggerAlarmAsync("E101", ct);
                break;

            default:
                await SendLineAsync(session, "ERR UNKNOWN_COMMAND", ct);
                Log($"收到指令：{command} → ERR UNKNOWN_COMMAND");
                break;
        }
    }

    /// <summary>
    /// 機台端的狀態轉換規則。主控端也有同樣的規則，這裡是第二道防線。
    /// </summary>
    /// <param name="command">START、PAUSE、STOP 或 RESET。</param>
    /// <returns>成功時為 "OK &lt;新狀態&gt;"；不允許時為 "ERR INVALID_STATE &lt;目前狀態&gt;"。</returns>
    private static string TryTransition(string command)
    {
        lock (StateGate)
        {
            string? next = (command, _machineState) switch
            {
                ("START", "IDLE" or "PAUSED") => "RUNNING",
                ("PAUSE", "RUNNING") => "PAUSED",
                ("STOP", "RUNNING" or "PAUSED") => "IDLE",
                ("RESET", "ALARM") => "IDLE",
                _ => null,
            };

            if (next is null)
                return $"ERR INVALID_STATE {_machineState}";

            _machineState = next;
            return $"OK {next}";
        }
    }

    /// <summary>
    /// 取得機台目前狀態。
    /// </summary>
    /// <returns>狀態文字，例如 "RUNNING"。</returns>
    private static string GetState()
    {
        lock (StateGate) return _machineState;
    }

    /// <summary>
    /// 送出一行文字（自動補 \n）。用鎖確保不會和其他執行緒（例如警報廣播）的資料交錯。
    /// </summary>
    /// <param name="session">要寫入的連線。</param>
    /// <param name="line">要送出的內容（不含 \n）。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>代表寫入動作的非同步工作。</returns>
    private static async Task SendLineAsync(ClientSession session, string line, CancellationToken ct)
    {
        await session.WriteLock.WaitAsync(ct);
        try
        {
            await session.Stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    /// <summary>
    /// 把一行文字故意分成兩段送出（中間停 300 毫秒），模擬半包。整個過程持有寫入鎖。
    /// </summary>
    /// <param name="session">要寫入的連線。</param>
    /// <param name="first">第一段。</param>
    /// <param name="second">第二段（會自動補 \n）。</param>
    /// <param name="ct">取消權杖。</param>
    /// <returns>代表寫入動作的非同步工作。</returns>
    private static async Task SendSplitAsync(ClientSession session, string first, string second, CancellationToken ct)
    {
        await session.WriteLock.WaitAsync(ct);
        try
        {
            await session.Stream.WriteAsync(Encoding.UTF8.GetBytes(first), ct);
            await Task.Delay(300, ct);
            await session.Stream.WriteAsync(Encoding.UTF8.GetBytes(second + "\n"), ct);
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    /// <summary>
    /// 在主控台印出帶有時間戳記（到毫秒）的訊息。
    /// </summary>
    /// <param name="message">要印出的訊息內容。</param>
    private static void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    /// <summary>
    /// 一條主控端連線：網路串流與寫入鎖。
    /// </summary>
    /// <param name="stream">這條連線的網路串流。</param>
    private sealed class ClientSession(NetworkStream stream)
    {
        /// <summary>取得這條連線的網路串流。</summary>
        public NetworkStream Stream { get; } = stream;

        /// <summary>取得寫入鎖：回應指令與廣播警報可能同時發生，要排隊寫入。</summary>
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }
}
