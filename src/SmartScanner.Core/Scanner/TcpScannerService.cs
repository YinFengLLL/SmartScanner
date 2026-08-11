using System.Net.Sockets;
using System.Text;
using SmartScanner.Core.Models;
using SmartScanner.Core.Serial;

namespace SmartScanner.Core.Scanner;

/// <summary>
/// 扫码枪 TCP/IP CLIENT 通讯（斯普锐 C20DE-RF10 TCP Server 模式）：
/// 上位机作为 TCP 客户端连接扫码枪 ScannerIp:ScannerPort；
/// 后台读取线程接收数据，复用 FrameParser 按 \r\n / \n / \r 切帧；
/// SendCommand 写 NetworkStream；对端关闭或网络异常触发 ConnectionChanged(false)。
/// 断线不自动重连（由界面"连接扫码枪"按钮手动重连）。
/// </summary>
public sealed class TcpScannerService : IScannerTransport
{
    private const int ConnectTimeoutMs = 3000;

    private readonly StringBuilder _buffer = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Thread? _readThread;

    /// <summary>收到一条完整条码（读取线程触发）</summary>
    public event Action<string>? BarcodeReceived;

    /// <summary>连接状态变化（IsOpen）</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsOpen => _client is { Connected: true } && _stream is not null;

    /// <summary>连接扫码枪；连接超时抛 TimeoutException</summary>
    public void Open(ScannerConfig config)
    {
        if (IsOpen)
        {
            return;
        }

        var tcp = new TcpClient();
        Task connectTask = tcp.ConnectAsync(config.ScannerIp, config.ScannerPort);
        if (!connectTask.Wait(TimeSpan.FromMilliseconds(ConnectTimeoutMs)))
        {
            tcp.Dispose();
            throw new TimeoutException($"连接扫码枪 {config.ScannerIp}:{config.ScannerPort} 超时");
        }

        var stream = tcp.GetStream();
        var cts = new CancellationTokenSource();
        _client = tcp;
        _stream = stream;
        _cts = cts;
        _buffer.Clear();
        _readThread = new Thread(() => ReadLoop(stream, cts.Token))
        {
            IsBackground = true,
            Name = "ScannerTcpRead",
        };
        _readThread.Start();
        ConnectionChanged?.Invoke(true);
    }

    public void Close()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            cts.Cancel();
        }

        _readThread?.Join(TimeSpan.FromSeconds(1));
        _readThread = null;

        var stream = Interlocked.Exchange(ref _stream, null);
        var tcp = Interlocked.Exchange(ref _client, null);
        try
        {
            stream?.Dispose();
        }
        catch (Exception)
        {
            // 释放即可
        }

        try
        {
            tcp?.Dispose();
        }
        catch (Exception)
        {
            // 释放即可
        }

        cts?.Dispose();
        _buffer.Clear();
        ConnectionChanged?.Invoke(false);
    }

    /// <summary>发送 ASCII 命令；未连接或写入失败返回 false（不抛异常）</summary>
    public bool SendCommand(string ascii)
    {
        var stream = _stream;
        if (stream is null || _client is not { Connected: true })
        {
            return false;
        }

        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes(ascii);
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ReadLoop(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = stream.Read(buf, 0, buf.Length);
                if (read == 0)
                {
                    break; // 对端关闭连接
                }

                string chunk = Encoding.ASCII.GetString(buf, 0, read);
                foreach (string frame in FrameParser.SplitFrames(chunk, _buffer))
                {
                    BarcodeReceived?.Invoke(frame);
                }
            }
        }
        catch (Exception)
        {
            // 网络异常（如对端强制断开）
        }
        finally
        {
            // 非主动关闭（Close 已置 CancellationRequested）才视为断线：清理连接状态并通知
            if (!ct.IsCancellationRequested)
            {
                var deadStream = Interlocked.Exchange(ref _stream, null);
                var deadClient = Interlocked.Exchange(ref _client, null);
                try
                {
                    deadStream?.Dispose();
                }
                catch (Exception)
                {
                    // 释放即可
                }

                try
                {
                    deadClient?.Dispose();
                }
                catch (Exception)
                {
                    // 释放即可
                }

                ConnectionChanged?.Invoke(false);
            }
        }
    }

    public void Dispose()
    {
        Close();
    }
}
