/*
 * 檔案：MainForm.cs
 * 專案：Controller.WinForms
 * 功能：主控程式的操作畫面。
 *       - 第一列：輸入 IP 與 Port，按「連線」或「中斷」
 *       - 第二列：選擇或輸入指令，按「送出」（或按 Enter），3 秒內沒回應顯示逾時
 *       - 中間：收發紀錄（→ 送出、← 收到、✗ 錯誤）
 *       - 下方：狀態列
 *       機台主動斷線時會在紀錄中顯示，程式不會當掉。
 *       控制項以程式碼建立（不使用設計工具），方便版本控制與閱讀。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.2（Slice 1 送指令、收回應）
 */

using Controller.Core.Connection;
using Controller.Core.Protocol;

namespace Controller.WinForms;

/// <summary>
/// 主控程式的主視窗。
/// </summary>
public class MainForm : Form
{
    /// <summary>指令等待回應的逾時時間。</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    /// <summary>設備 IP 輸入框。</summary>
    private readonly TextBox _hostTextBox = new() { Text = "127.0.0.1", Width = 140 };

    /// <summary>設備 Port 輸入框。</summary>
    private readonly NumericUpDown _portInput = new() { Minimum = 1, Maximum = 65535, Value = 5000, Width = 80 };

    /// <summary>「連線」按鈕。</summary>
    private readonly Button _connectButton = new() { Text = "連線", AutoSize = true };

    /// <summary>「中斷」按鈕，連線成功後才可按。</summary>
    private readonly Button _disconnectButton = new() { Text = "中斷", AutoSize = true, Enabled = false };

    /// <summary>指令輸入框，可下拉選擇常用指令，也可自行輸入。</summary>
    private readonly ComboBox _commandBox = new() { Width = 220, Text = "GET STATUS", Enabled = false };

    /// <summary>「送出」按鈕，連線成功後才可按。</summary>
    private readonly Button _sendButton = new() { Text = "送出", AutoSize = true, Enabled = false };

    /// <summary>收發紀錄清單。</summary>
    private readonly ListBox _logList = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    /// <summary>狀態列文字，顯示未連線、連線中、已連線或錯誤訊息。</summary>
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "未連線" };

    /// <summary>目前的 TCP 連線；未連線時為 null。</summary>
    private TcpDeviceConnection? _connection;

    /// <summary>目前的指令收發用戶端；未連線時為 null。</summary>
    private DeviceClient? _client;

    /// <summary>
    /// 建立主視窗：配置控制項並綁定按鈕事件。
    /// </summary>
    public MainForm()
    {
        Text = "Mini 設備控制器";
        ClientSize = new Size(560, 360);

        var connectionPanel = CreateRow(
            new Label { Text = "IP", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _hostTextBox,
            new Label { Text = "Port", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _portInput,
            _connectButton,
            _disconnectButton);

        _commandBox.Items.AddRange(new object[] { "GET STATUS", "SLEEP", "HELLO" });
        var commandPanel = CreateRow(
            new Label { Text = "指令", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _commandBox,
            _sendButton);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);

        // Dock 的排列順序：先加入 Fill，再加入上方的列；最後加入的 Top 會排在最上面
        Controls.Add(_logList);
        Controls.Add(commandPanel);
        Controls.Add(connectionPanel);
        Controls.Add(statusStrip);

        // 在指令框按 Enter 就等於按「送出」
        AcceptButton = _sendButton;

        _connectButton.Click += ConnectButton_Click;
        _disconnectButton.Click += DisconnectButton_Click;
        _sendButton.Click += SendButton_Click;
    }

    /// <summary>
    /// 建立一列由左到右排列的控制項面板，停靠在視窗上方。
    /// </summary>
    /// <param name="controls">要放進這一列的控制項。</param>
    /// <returns>建立好的面板。</returns>
    private static FlowLayoutPanel CreateRow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 6, 8, 0),
            WrapContents = false,
        };
        panel.Controls.AddRange(controls);
        return panel;
    }

    /// <summary>
    /// 「連線」按鈕事件：建立連線與指令用戶端，訂閱事件後非同步連線。
    /// </summary>
    /// <param name="sender">觸發事件的按鈕。</param>
    /// <param name="e">事件參數。</param>
    /// <remarks>
    /// 事件處理函式是 async void，因此所有例外都必須在這裡接住，否則程式會當掉。
    /// </remarks>
    private async void ConnectButton_Click(object? sender, EventArgs e)
    {
        // 連線中停用所有輸入，避免重複按下
        SetUiState(connected: false, busy: true);
        SetStatus("連線中…", Color.DarkOrange);

        string host = _hostTextBox.Text.Trim();
        int port = (int)_portInput.Value;
        TcpDeviceConnection? connection = null;
        DeviceClient? client = null;

        try
        {
            connection = new TcpDeviceConnection(host, port);
            client = new DeviceClient(connection);

            // 在連線前先訂閱事件，避免漏掉連線後馬上發生的事
            connection.Disconnected += OnConnectionLost;
            client.UnsolicitedLineReceived += OnUnsolicitedLine;

            await connection.ConnectAsync();

            _connection = connection;
            _client = client;
            SetUiState(connected: true, busy: false);
            SetStatus($"已連線 {host}:{port}", Color.Green);
            AppendLog($"已連線 {host}:{port}");
        }
        catch (Exception ex)
        {
            // 連線失敗：取消訂閱、釋放資源、顯示錯誤，程式繼續運作
            client?.Dispose();
            if (connection is not null)
            {
                connection.Disconnected -= OnConnectionLost;
                await connection.DisposeAsync();
            }

            SetUiState(connected: false, busy: false);
            SetStatus($"連線失敗：{ex.Message}", Color.Red);
        }
    }

    /// <summary>
    /// 「中斷」按鈕事件：關閉目前連線並恢復成未連線狀態。
    /// </summary>
    /// <param name="sender">觸發事件的按鈕。</param>
    /// <param name="e">事件參數。</param>
    private async void DisconnectButton_Click(object? sender, EventArgs e)
    {
        await CloseConnectionAsync();
        SetStatus("未連線", SystemColors.ControlText);
        AppendLog("已中斷連線");
    }

    /// <summary>
    /// 「送出」按鈕事件：送出指令並等待回應，結果寫入收發紀錄。
    /// </summary>
    /// <param name="sender">觸發事件的按鈕。</param>
    /// <param name="e">事件參數。</param>
    private async void SendButton_Click(object? sender, EventArgs e)
    {
        DeviceClient? client = _client;
        string command = _commandBox.Text.Trim();
        if (client is null || command.Length == 0) return;

        _sendButton.Enabled = false;
        AppendLog($"→ {command}");

        try
        {
            // await 期間 UI 執行緒是空的，畫面可以正常操作；
            // 結束後自動回到 UI 執行緒，這裡更新畫面不需要 Invoke
            string reply = await client.SendCommandAsync(command, CommandTimeout);
            AppendLog($"← {reply}");
        }
        catch (Exception ex)
        {
            // 逾時、斷線等錯誤只記錄，程式繼續運作
            AppendLog($"✗ {ex.Message}");
        }
        finally
        {
            // 等待期間如果斷線，_client 已被清成 null，按鈕就保持停用
            _sendButton.Enabled = _client is not null;
        }
    }

    /// <summary>
    /// 收到非預期訊息（例如逾時後才到的回應）時，寫入收發紀錄。
    /// </summary>
    /// <param name="line">收到的訊息。</param>
    /// <remarks>
    /// 此函式在「背景執行緒」被呼叫，不能直接操作控制項，
    /// 要用 BeginInvoke 把工作排回 UI 執行緒執行。
    /// 使用 BeginInvoke（非同步）而不是 Invoke（同步等待），避免接收執行緒被 UI 卡住。
    /// </remarks>
    private void OnUnsolicitedLine(string line)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(() => AppendLog($"← （非預期）{line}")));
    }

    /// <summary>
    /// 機台關閉連線或連線異常時，記錄原因並恢復成未連線狀態。
    /// </summary>
    /// <param name="error">造成中斷的例外；對方正常關閉時為 null。</param>
    /// <remarks>
    /// 此函式在「背景執行緒」被呼叫，用 BeginInvoke 切回 UI 執行緒。
    /// </remarks>
    private void OnConnectionLost(Exception? error)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(async () =>
        {
            AppendLog(error is null ? "✗ 機台關閉了連線" : $"✗ 連線中斷：{error.Message}");
            await CloseConnectionAsync();
            SetStatus("連線中斷", Color.Red);
        }));
    }

    /// <summary>
    /// 取消事件訂閱、釋放連線與用戶端，並把畫面切回未連線狀態。
    /// </summary>
    /// <returns>代表關閉動作的非同步工作。</returns>
    private async Task CloseConnectionAsync()
    {
        TcpDeviceConnection? connection = _connection;
        DeviceClient? client = _client;
        _connection = null;
        _client = null;

        client?.Dispose();
        if (connection is not null)
        {
            connection.Disconnected -= OnConnectionLost;
            await connection.DisposeAsync();
        }

        SetUiState(connected: false, busy: false);
    }

    /// <summary>
    /// 視窗關閉時釋放連線，避免留下未關閉的 socket。
    /// </summary>
    /// <param name="e">視窗關閉事件參數。</param>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _client?.Dispose();
        _connection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// 依連線狀態一次設定所有輸入控制項是否可用。
    /// </summary>
    /// <param name="connected">是否已連線。</param>
    /// <param name="busy">是否正在連線中（此時全部停用）。</param>
    private void SetUiState(bool connected, bool busy)
    {
        _hostTextBox.Enabled = !connected && !busy;
        _portInput.Enabled = !connected && !busy;
        _connectButton.Enabled = !connected && !busy;
        _disconnectButton.Enabled = connected && !busy;
        _commandBox.Enabled = connected && !busy;
        _sendButton.Enabled = connected && !busy;
    }

    /// <summary>
    /// 更新狀態列的文字與顏色。
    /// </summary>
    /// <param name="text">要顯示的狀態文字。</param>
    /// <param name="color">文字顏色，例如綠色表示已連線、紅色表示錯誤。</param>
    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    /// <summary>
    /// 在收發紀錄加入一筆帶時間戳記的訊息，並自動捲到最新一筆。
    /// </summary>
    /// <param name="text">要記錄的內容。</param>
    private void AppendLog(string text)
    {
        _logList.Items.Add($"{DateTime.Now:HH:mm:ss.fff}  {text}");
        _logList.TopIndex = _logList.Items.Count - 1;
    }
}
