/*
 * 檔案：MainForm.cs
 * 專案：Controller.WinForms
 * 功能：主控程式的操作畫面。
 *       - 第一列：輸入 IP 與 Port，按「連線」或「中斷」
 *       - 第二列：機台狀態（待機／運轉／暫停／警報，用顏色區分）與「啟動、暫停、停止、復歸」按鈕，
 *                 按鈕依目前狀態自動啟用或停用，不能按到當下不允許的動作
 *       - 第三列：手動指令（除錯用），3 秒內沒回應顯示逾時
 *       - 中間：收發紀錄（→ 送出、← 收到、✗ 錯誤、● 連線、◆ 機台）
 *       - 下方：狀態列
 *       連線由 ConnectionSupervisor 管理（心跳與自動重連），機台操作由 MachineController 管理（狀態機）。
 *       控制項以程式碼建立（不使用設計工具），方便版本控制與閱讀。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.4（Slice 3 機台狀態機）
 */

using Controller.Core.Connection;
using Controller.Core.Machine;

namespace Controller.WinForms;

/// <summary>
/// 主控程式的主視窗。
/// </summary>
public class MainForm : Form
{
    /// <summary>手動指令等待回應的逾時時間。</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    /// <summary>設備 IP 輸入框。</summary>
    private readonly TextBox _hostTextBox = new() { Text = "127.0.0.1", Width = 140 };

    /// <summary>設備 Port 輸入框。</summary>
    private readonly NumericUpDown _portInput = new() { Minimum = 1, Maximum = 65535, Value = 5000, Width = 80 };

    /// <summary>「連線」按鈕。</summary>
    private readonly Button _connectButton = new() { Text = "連線", AutoSize = true };

    /// <summary>「中斷」按鈕，已連線或重連中才可按。</summary>
    private readonly Button _disconnectButton = new() { Text = "中斷", AutoSize = true, Enabled = false };

    /// <summary>機台狀態顯示（文字與底色依狀態變化）。</summary>
    private readonly Label _machineStateLabel = new()
    {
        Text = "未連線",
        AutoSize = false,
        Width = 90,
        Height = 27,
        TextAlign = ContentAlignment.MiddleCenter,
        BorderStyle = BorderStyle.FixedSingle,
    };

    /// <summary>「啟動」按鈕（待機或暫停時可按）。</summary>
    private readonly Button _startButton = new() { Text = "啟動", AutoSize = true, Enabled = false };

    /// <summary>「暫停」按鈕（運轉時可按）。</summary>
    private readonly Button _pauseButton = new() { Text = "暫停", AutoSize = true, Enabled = false };

    /// <summary>「停止」按鈕（運轉或暫停時可按）。</summary>
    private readonly Button _stopButton = new() { Text = "停止", AutoSize = true, Enabled = false };

    /// <summary>「復歸」按鈕（警報時可按）。</summary>
    private readonly Button _resetButton = new() { Text = "復歸", AutoSize = true, Enabled = false };

    /// <summary>手動指令輸入框（除錯用），可下拉選擇常用指令，也可自行輸入。</summary>
    private readonly ComboBox _commandBox = new() { Width = 220, Text = "GET STATUS", Enabled = false };

    /// <summary>「送出」按鈕，已連線時才可按。</summary>
    private readonly Button _sendButton = new() { Text = "送出", AutoSize = true, Enabled = false };

    /// <summary>收發紀錄清單。</summary>
    private readonly ListBox _logList = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    /// <summary>狀態列文字。</summary>
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "未連線" };

    /// <summary>目前的連線監控器；未連線時為 null。</summary>
    private ConnectionSupervisor? _supervisor;

    /// <summary>目前的機台控制器；未連線時為 null。</summary>
    private MachineController? _machine;

    /// <summary>是否正在執行機台操作（執行中先停用所有機台按鈕，避免連按）。</summary>
    private bool _machineBusy;

    /// <summary>目前連線的 "IP:Port" 文字，顯示在狀態列。</summary>
    private string _endpointText = "";

    /// <summary>
    /// 建立主視窗：配置控制項並綁定按鈕事件。
    /// </summary>
    public MainForm()
    {
        Text = "Mini 設備控制器";
        ClientSize = new Size(620, 460);

        var connectionPanel = CreateRow(
            new Label { Text = "IP", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _hostTextBox,
            new Label { Text = "Port", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _portInput,
            _connectButton,
            _disconnectButton);

        var machinePanel = CreateRow(
            new Label { Text = "機台", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _machineStateLabel,
            _startButton,
            _pauseButton,
            _stopButton,
            _resetButton);

        _commandBox.Items.AddRange(new object[] { "GET STATUS", "ALARM", "PAUSE", "PING", "SLEEP", "HANG", "HELLO" });
        var commandPanel = CreateRow(
            new Label { Text = "指令", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _commandBox,
            _sendButton);

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);

        // Dock 的排列順序：先加入 Fill，再加入上方的列；最後加入的 Top 會排在最上面
        Controls.Add(_logList);
        Controls.Add(commandPanel);
        Controls.Add(machinePanel);
        Controls.Add(connectionPanel);
        Controls.Add(statusStrip);

        // 在指令框按 Enter 就等於按「送出」
        AcceptButton = _sendButton;

        _connectButton.Click += ConnectButton_Click;
        _disconnectButton.Click += DisconnectButton_Click;
        _sendButton.Click += SendButton_Click;
        _startButton.Click += (_, _) => ExecuteMachineCommand(MachineTrigger.Start);
        _pauseButton.Click += (_, _) => ExecuteMachineCommand(MachineTrigger.Pause);
        _stopButton.Click += (_, _) => ExecuteMachineCommand(MachineTrigger.Stop);
        _resetButton.Click += (_, _) => ExecuteMachineCommand(MachineTrigger.Reset);

        UpdateMachineUi();
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
    /// 「連線」按鈕事件：建立連線監控器與機台控制器，進行第一次連線。
    /// </summary>
    /// <param name="sender">觸發事件的按鈕。</param>
    /// <param name="e">事件參數。</param>
    /// <remarks>
    /// 事件處理函式是 async void，因此所有例外都必須在這裡接住，否則程式會當掉。
    /// 連上之後，連線監控器回報「已連線」時會自動與機台同步狀態（見 <see cref="OnSupervisorStatusChanged"/>）。
    /// </remarks>
    private async void ConnectButton_Click(object? sender, EventArgs e)
    {
        string host = _hostTextBox.Text.Trim();
        int port = (int)_portInput.Value;

        if (string.IsNullOrWhiteSpace(host))
        {
            SetStatus("請輸入 IP", Color.Red);
            return;
        }

        _endpointText = $"{host}:{port}";
        ApplyConnectionState(ConnectionState.Connecting);
        SetStatus("連線中…", Color.DarkOrange);

        // 每次（重新）連線都用工廠建立全新的 TCP 連線物件，避免沿用壞掉的連線
        var supervisor = new ConnectionSupervisor(() => new TcpDeviceConnection(host, port));
        var machine = new MachineController(supervisor);

        supervisor.StatusChanged += (state, message) => OnSupervisorStatusChanged(supervisor, state, message);
        supervisor.UnsolicitedLineReceived += line => OnUnsolicitedLine(supervisor, line);
        machine.Log += message => OnMachineLog(machine, message);
        machine.StateMachine.StateChanged += (from, to, reason) => OnMachineStateChanged(machine, from, to, reason);

        _supervisor = supervisor;
        _machine = machine;

        try
        {
            await supervisor.StartAsync();
        }
        catch (Exception ex)
        {
            // 第一次連線失敗：不自動重試，直接顯示錯誤讓使用者檢查 IP 與 Port
            _supervisor = null;
            _machine = null;
            await supervisor.DisposeAsync();
            ApplyConnectionState(ConnectionState.Disconnected);
            SetStatus($"連線失敗：{ex.Message}", Color.Red);
            AppendLog($"✗ 連線失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 「中斷」按鈕事件：停止心跳與自動重連，並中斷連線。
    /// </summary>
    /// <param name="sender">觸發事件的按鈕。</param>
    /// <param name="e">事件參數。</param>
    private async void DisconnectButton_Click(object? sender, EventArgs e)
    {
        ConnectionSupervisor? supervisor = _supervisor;
        if (supervisor is null) return;

        // 先清掉參考，之後這個監控器發出的事件都會被當成過期事件忽略
        _supervisor = null;
        _machine = null;
        _disconnectButton.Enabled = false;

        await supervisor.DisposeAsync();

        ApplyConnectionState(ConnectionState.Disconnected);
        SetStatus("未連線", SystemColors.ControlText);
        AppendLog("● 已中斷連線");
    }

    /// <summary>
    /// 機台按鈕（啟動、暫停、停止、復歸）共用的處理：交給機台控制器依狀態機規則執行。
    /// </summary>
    /// <param name="trigger">要執行的操作。</param>
    /// <remarks>
    /// 執行期間停用所有機台按鈕，避免連按；結果（成功、拒絕、失敗）由機台控制器的 Log 事件記錄。
    /// </remarks>
    private async void ExecuteMachineCommand(MachineTrigger trigger)
    {
        MachineController? machine = _machine;
        if (machine is null) return;

        _machineBusy = true;
        UpdateMachineUi();
        AppendLog($"→ {MachineController.Describe(trigger)}");

        try
        {
            await machine.ExecuteAsync(trigger);
        }
        catch (Exception ex)
        {
            AppendLog($"✗ {ex.Message}");
        }
        finally
        {
            _machineBusy = false;
            UpdateMachineUi();
        }
    }

    /// <summary>
    /// 「送出」按鈕事件：直接送出手動指令並等待回應（除錯用，不經過狀態機檢查）。
    /// </summary>
    /// <param name="sender">觸發事件的按鈕。</param>
    /// <param name="e">事件參數。</param>
    /// <remarks>
    /// 回應如果是機台狀態（例如 OK RUNNING），會同步到狀態機，避免畫面與機台不一致。
    /// </remarks>
    private async void SendButton_Click(object? sender, EventArgs e)
    {
        ConnectionSupervisor? supervisor = _supervisor;
        MachineController? machine = _machine;
        string command = _commandBox.Text.Trim();
        if (supervisor is null || command.Length == 0) return;

        _sendButton.Enabled = false;
        AppendLog($"→ {command}");

        try
        {
            string reply = await supervisor.SendCommandAsync(command, CommandTimeout);
            AppendLog($"← {reply}");

            if (machine is not null && MachineController.TryParseStatus(reply, out MachineState actual))
                machine.StateMachine.Synchronize(actual, $"手動指令 {command} 的回應");
        }
        catch (Exception ex)
        {
            // 逾時、斷線、重連中等錯誤只記錄，程式繼續運作
            AppendLog($"✗ {ex.Message}");
        }
        finally
        {
            _sendButton.Enabled = _supervisor?.State == ConnectionState.Connected;
        }
    }

    /// <summary>
    /// 連線監控器回報狀態時，更新紀錄、狀態列與按鈕；（重新）連上時與機台同步狀態。
    /// </summary>
    /// <param name="source">發出事件的監控器，用來過濾已經被中斷的舊監控器。</param>
    /// <param name="state">新的連線狀態。</param>
    /// <param name="message">說明文字。</param>
    /// <remarks>
    /// 此函式可能在「背景執行緒」被呼叫，用 <see cref="RunOnUi"/> 切回 UI 執行緒。
    /// </remarks>
    private void OnSupervisorStatusChanged(ConnectionSupervisor source, ConnectionState state, string message)
    {
        RunOnUi(async () =>
        {
            // 已經按過「中斷」的舊監控器，它的事件不再處理
            if (!ReferenceEquals(source, _supervisor)) return;

            AppendLog($"● {message}");
            ApplyConnectionState(state);

            switch (state)
            {
                case ConnectionState.Connected:
                    SetStatus($"已連線 {_endpointText}", Color.Green);

                    // 第一次連上或重連成功（不是心跳提醒）時，以機台為準同步狀態
                    if (!message.StartsWith("心跳", StringComparison.Ordinal) && _machine is { } machine)
                    {
                        await machine.SyncAsync();
                        UpdateMachineUi();
                    }
                    break;
                case ConnectionState.Connecting:
                    SetStatus("連線中…", Color.DarkOrange);
                    break;
                case ConnectionState.Reconnecting:
                    SetStatus($"重連中… {message}", Color.DarkOrange);
                    break;
                case ConnectionState.Disconnected:
                    SetStatus("未連線", SystemColors.ControlText);
                    break;
            }
        });
    }

    /// <summary>
    /// 收到非預期訊息時：機台事件（EVT ...）交給機台控制器，其他寫入紀錄。
    /// </summary>
    /// <param name="source">發出事件的監控器。</param>
    /// <param name="line">收到的訊息。</param>
    /// <remarks>
    /// 此函式在「背景執行緒」被呼叫，用 <see cref="RunOnUi"/> 切回 UI 執行緒。
    /// </remarks>
    private void OnUnsolicitedLine(ConnectionSupervisor source, string line)
    {
        RunOnUi(() =>
        {
            if (!ReferenceEquals(source, _supervisor)) return;

            AppendLog($"← {line}");
            if (_machine?.HandleEvent(line) != true)
                AppendLog("  （非預期訊息，例如逾時後才到的回應）");
        });
    }

    /// <summary>
    /// 機台控制器有紀錄時（拒絕、失敗、警報、同步），寫入收發紀錄。
    /// </summary>
    /// <param name="source">發出事件的機台控制器。</param>
    /// <param name="message">紀錄內容。</param>
    private void OnMachineLog(MachineController source, string message)
    {
        RunOnUi(() =>
        {
            if (ReferenceEquals(source, _machine))
                AppendLog($"◆ {message}");
        });
    }

    /// <summary>
    /// 機台狀態改變時，記錄轉換並更新狀態顯示與按鈕。
    /// </summary>
    /// <param name="source">發出事件的機台控制器。</param>
    /// <param name="from">原狀態。</param>
    /// <param name="to">新狀態。</param>
    /// <param name="reason">轉換原因。</param>
    private void OnMachineStateChanged(MachineController source, MachineState from, MachineState to, string reason)
    {
        RunOnUi(() =>
        {
            if (!ReferenceEquals(source, _machine)) return;

            AppendLog($"◆ 狀態：{MachineController.Describe(from)} → {MachineController.Describe(to)}（{reason}）");
            UpdateMachineUi();
        });
    }

    /// <summary>
    /// 在 UI 執行緒執行動作：目前已在 UI 執行緒就直接執行，否則用 BeginInvoke 排回 UI 執行緒。
    /// </summary>
    /// <param name="action">要執行的動作。</param>
    /// <remarks>
    /// 用 BeginInvoke（非同步）而不是 Invoke（同步等待），避免背景執行緒被 UI 卡住。
    /// </remarks>
    private void RunOnUi(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;

        if (InvokeRequired)
            BeginInvoke(action);
        else
            action();
    }

    /// <summary>
    /// 視窗關閉時停止監控並中斷連線，避免背景迴圈繼續重連。
    /// </summary>
    /// <param name="e">視窗關閉事件參數。</param>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ConnectionSupervisor? supervisor = _supervisor;
        _supervisor = null;
        _machine = null;
        supervisor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// 依連線狀態設定連線相關控制項是否可用，並更新機台區。
    /// </summary>
    /// <param name="state">目前的連線狀態。</param>
    /// <remarks>
    /// 未連線：可輸入 IP/Port、可按「連線」。
    /// 連線中：全部停用。
    /// 已連線：可按「中斷」、可送指令、可操作機台。
    /// 重連中：只能按「中斷」（停止重連）。
    /// </remarks>
    private void ApplyConnectionState(ConnectionState state)
    {
        bool idle = state == ConnectionState.Disconnected;
        bool connected = state == ConnectionState.Connected;
        bool reconnecting = state == ConnectionState.Reconnecting;

        _hostTextBox.Enabled = idle;
        _portInput.Enabled = idle;
        _connectButton.Enabled = idle;
        _disconnectButton.Enabled = connected || reconnecting;
        _commandBox.Enabled = connected;
        _sendButton.Enabled = connected;

        UpdateMachineUi();
    }

    /// <summary>
    /// 依機台狀態更新狀態顯示與四個機台按鈕：只有狀態機允許的操作才能按。
    /// </summary>
    private void UpdateMachineUi()
    {
        MachineController? machine = _machine;
        bool connected = _supervisor?.State == ConnectionState.Connected;

        if (machine is null || !connected)
        {
            _machineStateLabel.Text = machine is null ? "未連線" : "未知";
            _machineStateLabel.BackColor = SystemColors.Control;
            _machineStateLabel.ForeColor = SystemColors.GrayText;
            _startButton.Enabled = _pauseButton.Enabled = _stopButton.Enabled = _resetButton.Enabled = false;
            return;
        }

        MachineState state = machine.StateMachine.State;
        _machineStateLabel.Text = MachineController.Describe(state);
        (_machineStateLabel.BackColor, _machineStateLabel.ForeColor) = state switch
        {
            MachineState.Running => (Color.SeaGreen, Color.White),
            MachineState.Paused => (Color.Gold, Color.Black),
            MachineState.Alarm => (Color.Firebrick, Color.White),
            _ => (Color.Gainsboro, Color.Black),
        };

        // 第一道防線（畫面）：只有目前狀態允許的操作才能按
        bool canOperate = !_machineBusy;
        _startButton.Enabled = canOperate && machine.StateMachine.CanFire(MachineTrigger.Start);
        _pauseButton.Enabled = canOperate && machine.StateMachine.CanFire(MachineTrigger.Pause);
        _stopButton.Enabled = canOperate && machine.StateMachine.CanFire(MachineTrigger.Stop);
        _resetButton.Enabled = canOperate && machine.StateMachine.CanFire(MachineTrigger.Reset);
    }

    /// <summary>
    /// 更新狀態列的文字與顏色。
    /// </summary>
    /// <param name="text">要顯示的狀態文字。</param>
    /// <param name="color">文字顏色。</param>
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
