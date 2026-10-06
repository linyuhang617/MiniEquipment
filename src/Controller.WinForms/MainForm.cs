/*
 * 檔案：MainForm.cs
 * 專案：Controller.WinForms
 * 功能：主控程式的操作畫面。輸入 IP 與 Port 後按「連線」連上模擬機台，
 *       按「中斷」斷開連線，狀態列顯示目前連線狀態。
 *       連線過程使用 async/await，畫面不會凍結；連線失敗只顯示錯誤，程式不會當掉。
 *       控制項以程式碼建立（不使用設計工具），方便版本控制與閱讀。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.1（Slice 0 Walking Skeleton）
 */

using Controller.Core.Connection;

namespace Controller.WinForms;

/// <summary>
/// 主控程式的主視窗。
/// </summary>
public class MainForm : Form
{
    /// <summary>設備 IP 輸入框。</summary>
    private readonly TextBox _hostTextBox = new() { Text = "127.0.0.1", Width = 140 };

    /// <summary>設備 Port 輸入框。</summary>
    private readonly NumericUpDown _portInput = new() { Minimum = 1, Maximum = 65535, Value = 5000, Width = 80 };

    /// <summary>「連線」按鈕。</summary>
    private readonly Button _connectButton = new() { Text = "連線", AutoSize = true };

    /// <summary>「中斷」按鈕，連線成功後才可按。</summary>
    private readonly Button _disconnectButton = new() { Text = "中斷", AutoSize = true, Enabled = false };

    /// <summary>狀態列文字，顯示未連線、連線中、已連線或錯誤訊息。</summary>
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "未連線" };

    /// <summary>目前的設備連線；未連線時為 null。只依賴介面，之後換成序列埠也不用改畫面。</summary>
    private IDeviceConnection? _connection;

    /// <summary>
    /// 建立主視窗：配置控制項並綁定按鈕事件。
    /// </summary>
    public MainForm()
    {
        Text = "Mini 設備控制器";
        ClientSize = new Size(520, 140);

        // 用 FlowLayoutPanel 由左到右排列控制項，不用手動計算座標
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        panel.Controls.AddRange(new Control[]
        {
            new Label { Text = "IP", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _hostTextBox,
            new Label { Text = "Port", AutoSize = true, Margin = new Padding(3, 7, 3, 3) },
            _portInput,
            _connectButton,
            _disconnectButton,
        });

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);

        Controls.Add(panel);
        Controls.Add(statusStrip);

        _connectButton.Click += ConnectButton_Click;
        _disconnectButton.Click += DisconnectButton_Click;
    }

    /// <summary>
    /// 「連線」按鈕事件：依輸入的 IP 與 Port 非同步連線，並更新畫面狀態。
    /// </summary>
    /// <param name="sender">觸發事件的按鈕。</param>
    /// <param name="e">事件參數。</param>
    /// <remarks>
    /// 事件處理函式是 async void，因此所有例外都必須在這裡接住，否則程式會當掉。
    /// </remarks>
    private async void ConnectButton_Click(object? sender, EventArgs e)
    {
        // 連線中先停用輸入，避免重複按下
        SetInputsEnabled(false);
        SetStatus("連線中…", Color.DarkOrange);

        string host = _hostTextBox.Text.Trim();
        int port = (int)_portInput.Value;
        TcpDeviceConnection? connection = null;

        try
        {
            connection = new TcpDeviceConnection(host, port);

            // await 期間 UI 執行緒是空的，所以畫面不會凍結；
            // await 結束後會自動回到 UI 執行緒，這裡更新控制項不需要 Invoke
            await connection.ConnectAsync();

            _connection = connection;
            SetStatus($"已連線 {host}:{port}", Color.Green);
            _disconnectButton.Enabled = true;
        }
        catch (Exception ex)
        {
            // 連線失敗：釋放資源、顯示錯誤、恢復輸入，程式繼續運作
            if (connection is not null)
                await connection.DisposeAsync();

            SetStatus($"連線失敗：{ex.Message}", Color.Red);
            SetInputsEnabled(true);
        }
    }

    /// <summary>
    /// 「中斷」按鈕事件：關閉目前連線並恢復成未連線狀態。
    /// </summary>
    /// <param name="sender">觸發事件的按鈕。</param>
    /// <param name="e">事件參數。</param>
    private async void DisconnectButton_Click(object? sender, EventArgs e)
    {
        if (_connection is null) return;

        await _connection.DisposeAsync();
        _connection = null;

        SetStatus("未連線", SystemColors.ControlText);
        _disconnectButton.Enabled = false;
        SetInputsEnabled(true);
    }

    /// <summary>
    /// 視窗關閉時釋放連線，避免留下未關閉的 socket。
    /// </summary>
    /// <param name="e">視窗關閉事件參數。</param>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _connection?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnFormClosed(e);
    }

    /// <summary>
    /// 一次設定 IP、Port 輸入框與「連線」按鈕是否可用。
    /// </summary>
    /// <param name="enabled">true 為可用，false 為停用。</param>
    private void SetInputsEnabled(bool enabled)
    {
        _hostTextBox.Enabled = enabled;
        _portInput.Enabled = enabled;
        _connectButton.Enabled = enabled;
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
}
