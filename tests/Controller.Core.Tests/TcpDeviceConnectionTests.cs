/*
 * 檔案：TcpDeviceConnectionTests.cs
 * 專案：Controller.Core.Tests
 * 功能：TcpDeviceConnection 的單元測試。在本機開一個臨時的 TcpListener 當作設備，
 *       驗證「能連上」、「沒有設備時連線失敗」、「中斷後狀態正確」三種情況。
 *
 * @author  linyuhang617
 * @since   2026-10-06
 * @version 0.1（Slice 0 Walking Skeleton）
 */

using System.Net;
using System.Net.Sockets;
using Controller.Core.Connection;

namespace Controller.Core.Tests;

/// <summary>
/// <see cref="TcpDeviceConnection"/> 的單元測試。
/// </summary>
public class TcpDeviceConnectionTests
{
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
        // Arrange
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var connection = new TcpDeviceConnection("127.0.0.1", PortOf(listener));
            await connection.ConnectAsync();

            // Act
            await connection.DisconnectAsync();

            // Assert
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
