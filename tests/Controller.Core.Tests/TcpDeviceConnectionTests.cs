/*
 * 檔案：TcpDeviceConnectionTests.cs
 * 專案：Controller.Core.Tests
 * 功能：TcpDeviceConnection 的單元測試。在本機開一個臨時的 TcpListener 當作設備，
 *       驗證連線、連線失敗、中斷、分段收訊、送出訊息、對方斷線等情況。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.2（Slice 1 送指令、收回應）
 */

using System.Net;
using System.Net.Sockets;
using System.Text;
using Controller.Core.Connection;

namespace Controller.Core.Tests;

/// <summary>
/// <see cref="TcpDeviceConnection"/> 的單元測試。
/// </summary>
public class TcpDeviceConnectionTests
{
    /// <summary>等待非同步事件的最長時間，避免測試失敗時卡住。</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 有設備在監聽時，ConnectAsync 應成功，且 IsConnected 為 true。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task ConnectAsync_ServerListening_IsConnected()
    {
        // Arrange：port 0 代表讓系統自動挑一個沒被使用的 port
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var connection = new TcpDeviceConnection("127.0.0.1", PortOf(listener));

            // Act
            await connection.ConnectAsync();

            // Assert
            Assert.True(connection.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 沒有設備在監聽時，ConnectAsync 應拋出 SocketException，且 IsConnected 為 false。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task ConnectAsync_NoServer_ThrowsAndNotConnected()
    {
        // Arrange：先開再關，取得一個確定沒人在監聽的 port
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = PortOf(listener);
        listener.Stop();

        await using var connection = new TcpDeviceConnection("127.0.0.1", port);

        // Act & Assert
        await Assert.ThrowsAsync<SocketException>(() => connection.ConnectAsync());
        Assert.False(connection.IsConnected);
    }

    /// <summary>
    /// 連線後呼叫 DisconnectAsync，IsConnected 應變回 false。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task DisconnectAsync_AfterConnect_IsNotConnected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var connection = new TcpDeviceConnection("127.0.0.1", PortOf(listener));
            await connection.ConnectAsync();

            await connection.DisconnectAsync();

            Assert.False(connection.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 設備把一筆訊息分兩次送出，應等湊齊後才觸發一次 LineReceived，內容完整。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task ServerSendsLineInTwoParts_LineReceivedOnceComplete()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var connection = new TcpDeviceConnection("127.0.0.1", PortOf(listener));
            var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.LineReceived += line => received.TrySetResult(line);

            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            await connection.ConnectAsync();
            using TcpClient server = await acceptTask;
            server.NoDelay = true;
            NetworkStream stream = server.GetStream();

            // 真的分兩次送，中間停一下
            await stream.WriteAsync(Encoding.UTF8.GetBytes("OK "));
            await Task.Delay(100);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("IDLE\n"));

            Assert.Equal("OK IDLE", await received.Task.WaitAsync(WaitLimit));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// SendLineAsync 送出的訊息，設備端應收到完整的一行（結尾自動補 \n）。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task SendLineAsync_ServerReceivesLineWithNewline()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var connection = new TcpDeviceConnection("127.0.0.1", PortOf(listener));
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            await connection.ConnectAsync();
            using TcpClient server = await acceptTask;
            using var reader = new StreamReader(server.GetStream(), Encoding.UTF8);

            await connection.SendLineAsync("GET STATUS");

            Assert.Equal("GET STATUS", await reader.ReadLineAsync().WaitAsync(WaitLimit));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 設備端關閉連線時，應觸發 Disconnected，且 IsConnected 變為 false。
    /// </summary>
    /// <returns>非同步測試工作。</returns>
    [Fact]
    public async Task ServerClosesConnection_RaisesDisconnected()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var connection = new TcpDeviceConnection("127.0.0.1", PortOf(listener));
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Disconnected += _ => disconnected.TrySetResult();

            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            await connection.ConnectAsync();
            TcpClient server = await acceptTask;

            // 模擬機台關掉連線
            server.Dispose();

            await disconnected.Task.WaitAsync(WaitLimit);
            Assert.False(connection.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// 取得 TcpListener 實際使用的 port。
    /// </summary>
    /// <param name="listener">已啟動的 TcpListener。</param>
    /// <returns>系統分配的 port 號碼。</returns>
    private static int PortOf(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
}
