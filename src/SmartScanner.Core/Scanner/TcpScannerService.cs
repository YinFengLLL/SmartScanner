using System.Net.Sockets;
using System.Text;
using SmartScanner.Core.Models;
using SmartScanner.Core.Serial;

namespace SmartScanner.Core.Scanner;

/// <summary>
/// 扫码枪 TCP/IP CLIENT 通讯（斯普锐 C20DE-RF10 TCP Server 模式）：
/// 上位机作为 TCP 客户端连接扫码枪 ScannerIp:ScannerPort；
/// 后台读取线程接收数据，按配置的切帧方式（自动 / CRLF / CR / LF / 无前后缀）切出条码：
/// 带后缀的按后缀切帧；扫码枪返回信息不带前缀和后缀时，数据静默达到设定时间即把缓冲内容当成一帧。
/// SendCommand 写 NetworkStream；对端关闭或网络异常触发 ConnectionChanged(false)。
/// 断线不自动重连（由界面"连接扫码枪"按钮手动重连）。
/// </summary>
public sealed class TcpScannerService : IScannerTransport
{
    private const int ConnectTimeoutMs = 3000;

    /// <summary>等待可读数据的轮询间隔（用于及时发现"数据已静默"）</summary>
    private const int PollSliceMs = 50;

    private readonly StringBuilder _buffer = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Thread? _readThread;
    private long _lastDataTicks;                                  // 最近一次收到数据的时刻（Environment.TickCount64）
    private ScannerFrameMode _frameMode = ScannerFrameMode.Auto;   // 前后缀（结束符）识别方式
    private int _idleFlushMs = 150;                               // 无结束符时的静默分帧时间
    private bool _trimAffixes = true;                             // 是否去掉前后缀不可见字符

    /// <summary>收到一条完整条码（读取线程触发）</summary>
    public event Action<string>? BarcodeReceived;

    /// <summary>收到扫码枪原始字节（解码前的文本块，用于现场排查数据通道；读取线程触发）</summary>
    public event Action<string>? RawDataReceived;

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

        // 前后缀（结束符）识别方式随配置生效
        _frameMode = config.FrameMode;
        _idleFlushMs = FrameParser.NormalizeIdleFlushMs(config.FrameIdleFlushMs);
        _trimAffixes = config.TrimFrameAffixes;

        var stream = tcp.GetStream();
        var cts = new CancellationTokenSource();
        _client = tcp;
        _stream = stream;
        _cts = cts;
        _buffer.Clear();
        _lastDataTicks = 0;
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
        Socket? socket = _client?.Client;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 无数据可读：若缓冲里有残留内容且已静默 IdleFlushMs，说明该口径没有结尾符 → 直接出帧
                if (!stream.DataAvailable)
                {
                    FlushIdleFrame();
                    if (socket is null || !socket.Poll(PollSliceMs * 1000, SelectMode.SelectRead))
                    {
                        continue; // 等待超时/取消，回到循环顶部检查空闲
                    }
                }

                int read = stream.Read(buf, 0, buf.Length);
                if (read == 0)
                {
                    break; // 对端关闭连接
                }

                _lastDataTicks = Environment.TickCount64;
                string chunk = Encoding.ASCII.GetString(buf, 0, read);

                // 原始字节先上报（现场可据此确认数据通道是否通）
                RawDataReceived?.Invoke(chunk);

                foreach (string frame in FrameParser.SplitFrames(chunk, _buffer, _frameMode))
                {
                    EmitFrame(frame);
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

    /// <summary>
    /// 数据静默回调：缓冲里还有内容说明扫码枪发了数据但没有（或没等到）结束符，
    /// 静默超过设定时间（默认 150ms）后把缓冲内容当成一帧上报。
    /// </summary>
    private void FlushIdleFrame()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        if (Environment.TickCount64 - _lastDataTicks < _idleFlushMs)
        {
            return; // 还在持续收数据（分包未结束），继续等
        }

        string pending = _buffer.ToString();
        _buffer.Clear();
        EmitFrame(pending);
    }

    /// <summary>按"去前后缀"配置清理边界字符（STX/ETX/NUL/CR/LF 等）后上报一条条码；空帧丢弃</summary>
    private void EmitFrame(string raw)
    {
        string barcode = FrameParser.CleanFrame(raw, _trimAffixes);
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            BarcodeReceived?.Invoke(barcode);
        }
    }

    public void Dispose()
    {
        Close();
    }
}
