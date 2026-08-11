using System.IO.Ports;
using System.Text;
using SmartScanner.Core.Models;
using SmartScanner.Core.Scanner;

namespace SmartScanner.Core.Serial;

/// <summary>
/// 串口帧解析：按 \r\n 或 \n 结尾切分 ASCII 完整帧。
/// 纯函数设计，便于单元测试。
/// </summary>
public static class FrameParser
{
    /// <summary>
    /// 把新到的字符块追加进 <paramref name="carry"/> 缓冲，切出所有完整帧（以 \n、\r\n 或单独 \r 结尾），
    /// 未完成的部分保留在缓冲中。返回完整帧列表。
    /// 兼容斯普锐等扫码枪可配置的 CR / CRLF / LF 三种行结束符。
    /// </summary>
    public static IReadOnlyList<string> SplitFrames(string chunk, StringBuilder carry)
    {
        if (!string.IsNullOrEmpty(chunk))
        {
            carry.Append(chunk);
        }

        var frames = new List<string>();
        while (true)
        {
            string pending = carry.ToString();
            int idxLf = pending.IndexOf('\n');
            int idxCr = pending.IndexOf('\r');

            // 取最早出现的行结束符；两者都没有则等待更多数据
            if (idxLf < 0 && idxCr < 0)
            {
                break;
            }

            int idx = idxLf >= 0 && idxCr >= 0 ? Math.Min(idxLf, idxCr) : Math.Max(idxLf, idxCr);
            string line = pending[..idx];
            carry.Remove(0, idx + 1);

            // 若刚消费的是 \r 且紧跟着 \n（即 \r\n），一并消费 \n，避免产生空帧
            if (pending[idx] == '\r' && carry.Length > 0 && carry[0] == '\n')
            {
                carry.Remove(0, 1);
            }

            if (line.Length > 0)
            {
                frames.Add(line);
            }
        }

        return frames;
    }
}

/// <summary>
/// 扫码枪串口（USB COM）服务：打开/关闭串口、接收 ASCII 条码（以 \r\n 或 \n 结尾）、
/// 发送 ASCII 命令（TRIGGER/RELEASE）、动态枚举 COM 口。实现 IScannerTransport。
/// 事件在串口接收线程触发，UI 层需自行 BeginInvoke 调度。
/// </summary>
public sealed class SerialScannerService : IScannerTransport
{
    private readonly StringBuilder _buffer = new();
    private SerialPort? _port;

    /// <summary>收到一条完整条码（已在接收线程触发）</summary>
    public event Action<string>? BarcodeReceived;

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
        port.DataReceived += OnDataReceived;
        port.Open();
        _port = port;
        _buffer.Clear();
        ConnectionChanged?.Invoke(true);
    }

    public void Close()
    {
        if (_port is null)
        {
            return;
        }

        _port.DataReceived -= OnDataReceived;
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
            _buffer.Clear();
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

            foreach (string frame in FrameParser.SplitFrames(chunk, _buffer))
            {
                BarcodeReceived?.Invoke(frame);
            }
        }
        catch (Exception)
        {
            // 串口异常（如拔线）不崩溃，交由状态日志层处理
            ConnectionChanged?.Invoke(false);
        }
    }

    public void Dispose()
    {
        Close();
    }
}
