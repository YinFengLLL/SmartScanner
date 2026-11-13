using System.IO.Ports;
using System.Text;
using SmartScanner.Core.Models;
using SmartScanner.Core.Scanner;

namespace SmartScanner.Core.Serial;

/// <summary>
/// 扫码枪数据帧解析（前后缀识别）：
/// 按配置的结束符方式切帧（自动 / CRLF / CR / LF / 无前后缀），
/// "无前后缀"或超时收不到结束符时，由传输层在数据静默后把缓冲内容当成一帧
/// （见 TcpScannerService / SerialScannerService）。
/// 纯函数设计，便于单元测试。
/// </summary>
public static class FrameParser
{
    /// <summary>
    /// 把新到的字符块追加进 <paramref name="carry"/> 缓冲，按"自动识别"切出所有完整帧
    /// （\r\n、单独 \r、单独 \n 均可）；未完成的部分保留在缓冲中。
    /// </summary>
    public static IReadOnlyList<string> SplitFrames(string chunk, StringBuilder carry)
        => SplitFrames(chunk, carry, ScannerFrameMode.Auto);

    /// <summary>
    /// 把新到的字符块追加进 <paramref name="carry"/> 缓冲，按 <paramref name="mode"/> 指定结束符切帧，
    /// 未完成的部分保留在缓冲中。<see cref="ScannerFrameMode.None"/> 表示不带前后缀，只能靠静默分帧。
    /// </summary>
    public static IReadOnlyList<string> SplitFrames(string chunk, StringBuilder carry, ScannerFrameMode mode)
    {
        if (!string.IsNullOrEmpty(chunk))
        {
            carry.Append(chunk);
        }

        var frames = new List<string>();
        if (mode == ScannerFrameMode.None)
        {
            return frames; // 无前后缀：全部留给静默分帧处理
        }

        while (true)
        {
            string pending = carry.ToString();
            if (!TryFindTerminator(pending, mode, out int idx, out int length))
            {
                break; // 还没有结束符，等待更多数据
            }

            string line = pending[..idx];
            carry.Remove(0, idx + length);

            if (line.Length > 0)
            {
                frames.Add(line);
            }
        }

        return frames;
    }

    /// <summary>按切帧方式查找结束符位置；<paramref name="length"/> 为结束符长度（CRLF=2，CR/LF=1）</summary>
    private static bool TryFindTerminator(string pending, ScannerFrameMode mode, out int index, out int length)
    {
        switch (mode)
        {
            case ScannerFrameMode.CrLf:
            {
                index = pending.IndexOf("\r\n", StringComparison.Ordinal);
                length = 2;
                return index >= 0;
            }

            case ScannerFrameMode.Cr:
            {
                index = pending.IndexOf('\r');
                length = 1;
                return index >= 0;
            }

            case ScannerFrameMode.Lf:
            {
                index = pending.IndexOf('\n');
                length = 1;
                return index >= 0;
            }

            default: // Auto：CR / LF / CRLF 都算结束符，取最早出现的
            {
                int idxLf = pending.IndexOf('\n');
                int idxCr = pending.IndexOf('\r');
                if (idxLf < 0 && idxCr < 0)
                {
                    index = -1;
                    length = 0;
                    return false;
                }

                index = idxLf >= 0 && idxCr >= 0 ? Math.Min(idxLf, idxCr) : Math.Max(idxLf, idxCr);
                bool isCrLfPair = pending[index] == '\r'
                    && index + 1 < pending.Length
                    && pending[index + 1] == '\n';
                length = isCrLfPair ? 2 : 1;
                return true;
            }
        }
    }

    /// <summary>
    /// 清理一帧的边界噪声：去掉两端不可见字符（STX 0x02 / ETX 0x03 / NUL / CR / LF / 空格等前缀、后缀）。
    /// <paramref name="trimAffixes"/>=false 时原样返回（仅用于现场排查原始内容）。
    /// </summary>
    public static string CleanFrame(string? raw, bool trimAffixes = true)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (!trimAffixes)
        {
            return raw;
        }

        int start = 0;
        int end = raw.Length - 1;
        while (start <= end && IsFrameNoise(raw[start]))
        {
            start++;
        }

        while (end >= start && IsFrameNoise(raw[end]))
        {
            end--;
        }

        return start > end ? string.Empty : raw.Substring(start, end - start + 1);
    }

    /// <summary>帧边界噪声字符：所有控制字符（含 STX/ETX/NUL/CR/LF）与空格</summary>
    private static bool IsFrameNoise(char c) => c <= 0x20 || c == 0x7F;

    /// <summary>静默分帧时间归一化：0/负数回退默认 150ms，取值范围 50~5000ms</summary>
    public static int NormalizeIdleFlushMs(int milliseconds)
        => milliseconds <= 0 ? 150 : Math.Clamp(milliseconds, 50, 5000);
}

/// <summary>
/// 扫码枪串口（USB COM）服务：打开/关闭串口、接收 ASCII 条码（以 \r\n / \n / \r 结尾，
/// 或无结尾符时按数据静默切帧）、发送 ASCII 命令（TRIGGER/RELEASE）、动态枚举 COM 口。
/// 实现 IScannerTransport。事件在串口接收线程触发，UI 层需自行 BeginInvoke 调度。
/// </summary>
public sealed class SerialScannerService : IScannerTransport
{
    private readonly StringBuilder _buffer = new();
    private readonly object _bufferLock = new();
    private SerialPort? _port;
    private Timer? _idleFlushTimer;
    private ScannerFrameMode _frameMode = ScannerFrameMode.Auto; // 前后缀（结束符）识别方式
    private int _idleFlushMs = 150;                              // 无结束符时的静默分帧时间
    private bool _trimAffixes = true;                            // 是否去掉前后缀不可见字符

    /// <summary>收到一条完整条码（已在接收线程触发）</summary>
    public event Action<string>? BarcodeReceived;

    /// <summary>收到扫码枪原始字节（解码前的文本块，用于现场排查数据通道；接收线程触发）</summary>
    public event Action<string>? RawDataReceived;

    /// <summary>连接状态变化（IsOpen）</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsOpen => _port is { IsOpen: true };

    /// <summary>
    /// 向扫码枪发送 ASCII 命令（如 TRIGGER / RELEASE）。
    /// 串口未打开或写入失败返回 false（不抛异常，由调用方记录日志）。
    /// </summary>
    public bool SendCommand(string asciiCommand)
    {
        if (_port is not { IsOpen: true })
        {
            return false;
        }

        try
        {
            _port.Write(asciiCommand);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>动态扫描可用 COM 口，按自然顺序（COM2 &lt; COM10）排序</summary>
    public static string[] GetAvailablePorts()
    {
        return SerialPort.GetPortNames()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>按配置打开串口；失败抛出异常并由调用方记录日志</summary>
    public void Open(ScannerConfig config)
    {
        if (IsOpen)
        {
            return;
        }

        var port = new SerialPort(config.ComPort, config.BaudRate, config.Parity, config.DataBits, config.StopBits)
        {
            Encoding = Encoding.ASCII,
            ReadTimeout = 500,
            WriteTimeout = 500,
        };
        // 前后缀（结束符）识别方式随配置生效
        _frameMode = config.FrameMode;
        _idleFlushMs = FrameParser.NormalizeIdleFlushMs(config.FrameIdleFlushMs);
        _trimAffixes = config.TrimFrameAffixes;

        port.DataReceived += OnDataReceived;
        port.Open();
        _port = port;
        lock (_bufferLock)
        {
            _buffer.Clear();
        }

        _idleFlushTimer ??= new Timer(_ => FlushIdleFrame(), null, Timeout.Infinite, Timeout.Infinite);
        ConnectionChanged?.Invoke(true);
    }

    public void Close()
    {
        if (_port is null)
        {
            return;
        }

        _port.DataReceived -= OnDataReceived;
        _idleFlushTimer?.Dispose();
        _idleFlushTimer = null;
        try
        {
            _port.Close();
        }
        catch (Exception)
        {
            // 关闭失败不抛出，释放即可
        }
        finally
        {
            _port.Dispose();
            _port = null;
            lock (_bufferLock)
            {
                _buffer.Clear();
            }

            ConnectionChanged?.Invoke(false);
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port is not { IsOpen: true })
        {
            return;
        }

        try
        {
            int available = _port.BytesToRead;
            if (available <= 0)
            {
                return;
            }

            var buf = new byte[available];
            int read = _port.Read(buf, 0, available);
            string chunk = Encoding.ASCII.GetString(buf, 0, read);

            // 原始字节先上报（现场可据此确认数据通道是否通）
            RawDataReceived?.Invoke(chunk);

            List<string> frames;
            lock (_bufferLock)
            {
                frames = FrameParser.SplitFrames(chunk, _buffer, _frameMode).ToList();
            }

            foreach (string frame in frames)
            {
                EmitFrame(frame);
            }

            // 无前后缀（或没等到结束符）时：静默一段时间后把残留缓冲当成一帧
            _idleFlushTimer?.Change(_idleFlushMs, Timeout.Infinite);
        }
        catch (Exception)
        {
            // 串口异常（如拔线）不崩溃，交由状态日志层处理
            ConnectionChanged?.Invoke(false);
        }
    }

    /// <summary>数据静默回调：缓冲里还有内容说明该口径没有结尾符，直接作为一帧上报</summary>
    private void FlushIdleFrame()
    {
        if (_port is not { IsOpen: true })
        {
            return;
        }

        string pending;
        lock (_bufferLock)
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            pending = _buffer.ToString();
            _buffer.Clear();
        }

        EmitFrame(pending);
    }

    /// <summary>按"去前后缀"配置清理边界字符后上报一条条码（空帧丢弃）</summary>
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
