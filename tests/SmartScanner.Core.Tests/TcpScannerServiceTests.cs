using System.Net;
using System.Net.Sockets;
using System.Text;
using SmartScanner.Core.Models;
using SmartScanner.Core.Scanner;
using SmartScanner.Core.Serial;

namespace SmartScanner.Core.Tests;

/// <summary>
/// TcpScannerService 与本地 TCP 监听（模拟扫码枪 TCP Server 模式）的集成测试：
/// 连接、接收条码帧、发送命令、断线事件、未连接安全降级。
/// </summary>
public sealed class TcpScannerServiceTests : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _port;
    private TcpClient? _serverSide;

    public TcpScannerServiceTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _serverSide?.Dispose();
        _listener.Stop();
    }

    private ScannerConfig NewConfig(ScannerFrameMode mode = ScannerFrameMode.Auto, bool trimAffixes = true) => new()
    {
        TransportMode = ScannerTransportMode.Tcp,
        ScannerIp = "127.0.0.1",
        ScannerPort = _port,
        FrameMode = mode,
        TrimFrameAffixes = trimAffixes,
    };

    /// <summary>异步接受服务端连接（模拟扫码枪 TCP Server 侧），带超时</summary>
    private async Task<TcpClient> AcceptServerSideAsync()
    {
        var server = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        _serverSide = server;
        return server;
    }

    [Fact]
    public async Task 连接成功_IsOpen为true()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig());
        await acceptTask;
        Assert.True(service.IsOpen);
        service.Close();
    }

    [Fact]
    public async Task 服务端发送条码_触发BarcodeReceived()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig());
        await acceptTask;

        string? received = null;
        var evt = new ManualResetEventSlim();
        service.BarcodeReceived += b =>
        {
            received = b;
            evt.Set();
        };

        var stream = _serverSide!.GetStream();
        byte[] data = Encoding.ASCII.GetBytes("ABC123\r\n");
        stream.Write(data, 0, data.Length);

        Assert.True(evt.Wait(TimeSpan.FromSeconds(5)), "未收到条码");
        Assert.Equal("ABC123", received);
        service.Close();
    }

    [Fact]
    public async Task 无前缀无后缀条码_静默后也能识别()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig());
        await acceptTask;

        string? received = null;
        string? raw = null;
        var evt = new ManualResetEventSlim();
        service.RawDataReceived += r => raw = r;
        service.BarcodeReceived += b =>
        {
            received = b;
            evt.Set();
        };

        // 现场扫码枪返回信息不带前缀和后缀（无 CR/LF）
        byte[] data = Encoding.ASCII.GetBytes("6901234567890");
        var stream = _serverSide!.GetStream();
        stream.Write(data, 0, data.Length);

        Assert.True(evt.Wait(TimeSpan.FromSeconds(3)), "无结尾符条码未被识别（帧无法切分）");
        Assert.Equal("6901234567890", received);
        Assert.Equal("6901234567890", raw);
        service.Close();
    }

    [Fact]
    public async Task STX_ETX包裹且无CRLF_也能识别()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig());
        await acceptTask;

        string? received = null;
        var evt = new ManualResetEventSlim();
        service.BarcodeReceived += b =>
        {
            received = b;
            evt.Set();
        };

        byte[] payload = Encoding.ASCII.GetBytes("ABC123");
        byte[] data = [0x02, .. payload, 0x03];
        var stream = _serverSide!.GetStream();
        stream.Write(data, 0, data.Length);

        Assert.True(evt.Wait(TimeSpan.FromSeconds(3)), "STX/ETX 包裹条码未被识别");
        Assert.Equal("ABC123", received);
        service.Close();
    }

    [Fact]
    public async Task 连续两次无结尾符扫码_静默分隔为两帧()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig());
        await acceptTask;

        var received = new List<string>();
        var twoFrames = new ManualResetEventSlim();
        service.BarcodeReceived += b =>
        {
            lock (received)
            {
                received.Add(b);
                if (received.Count == 2)
                {
                    twoFrames.Set();
                }
            }
        };

        var stream = _serverSide!.GetStream();
        byte[] first = Encoding.ASCII.GetBytes("A1");
        stream.Write(first, 0, first.Length);
        Thread.Sleep(500); // 模拟两次扫码之间的静默
        byte[] second = Encoding.ASCII.GetBytes("B2");
        stream.Write(second, 0, second.Length);

        Assert.True(twoFrames.Wait(TimeSpan.FromSeconds(3)), "两次无结尾符扫码未被切成两帧");
        lock (received)
        {
            Assert.Equal(["A1", "B2"], received);
        }

        service.Close();
    }

    [Fact]
    public async Task 无前后缀模式_按静默时间分帧()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig(ScannerFrameMode.None));
        await acceptTask;

        string? received = null;
        var evt = new ManualResetEventSlim();
        service.BarcodeReceived += b =>
        {
            received = b;
            evt.Set();
        };

        byte[] data = Encoding.ASCII.GetBytes("6901234567890");
        var stream = _serverSide!.GetStream();
        stream.Write(data, 0, data.Length);

        Assert.True(evt.Wait(TimeSpan.FromSeconds(3)), "无前后缀模式未按静默时间分帧");
        Assert.Equal("6901234567890", received);
        service.Close();
    }

    [Fact]
    public async Task 关闭去前后缀_不可见字符原样保留()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig(ScannerFrameMode.None, trimAffixes: false));
        await acceptTask;

        string? received = null;
        var evt = new ManualResetEventSlim();
        service.BarcodeReceived += b =>
        {
            received = b;
            evt.Set();
        };

        byte[] payload = Encoding.ASCII.GetBytes("ABC123");
        byte[] data = [0x02, .. payload, 0x03];
        var stream = _serverSide!.GetStream();
        stream.Write(data, 0, data.Length);

        Assert.True(evt.Wait(TimeSpan.FromSeconds(3)), "未收到条码");
        Assert.Equal("\u0002ABC123\u0003", received); // 关闭"去前后缀"后原样上报
        service.Close();
    }

    [Fact]
    public async Task SendCommand_服务端收到TRIGGER字节()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig());
        await acceptTask;

        Assert.True(service.SendCommand(ScannerCommand.Trigger));

        var buf = new byte[7];
        int read = _serverSide!.GetStream().Read(buf, 0, buf.Length);
        Assert.Equal("TRIGGER", Encoding.ASCII.GetString(buf, 0, read));
        service.Close();
    }

    [Fact]
    public async Task 服务端关闭连接_触发断线事件()
    {
        using var service = new TcpScannerService();
        var acceptTask = AcceptServerSideAsync();
        service.Open(NewConfig());
        await acceptTask;

        var disconnected = new ManualResetEventSlim();
        service.ConnectionChanged += open =>
        {
            if (!open)
            {
                disconnected.Set();
            }
        };

        _serverSide!.Close(); // 扫码枪侧断开

        Assert.True(disconnected.Wait(TimeSpan.FromSeconds(5)), "未收到断线事件");
        Assert.False(service.IsOpen);
        service.Close();
    }

    [Fact]
    public void 未连接时发送命令返回false()
    {
        using var service = new TcpScannerService();
        Assert.False(service.SendCommand(ScannerCommand.Trigger));
    }
}
