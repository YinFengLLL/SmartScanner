using System.Text;
using SmartScanner.Core.Data;
using SmartScanner.Core.Events;
using SmartScanner.Core.Handshake;
using SmartScanner.Core.Models;
using SmartScanner.Core.Modbus;
using SmartScanner.Core.Scanner;
using SmartScanner.Core.Serial;

namespace SmartScanner.Core.Controller;

/// <summary>
/// 扫码流程编排：
/// 串口接收的条码显示在 UI，并在扫码 OK 时按配置方式（INT16/INT32/STRING）写入"条码内容寄存器"；
/// PLC 交互通过保持寄存器（输入触发 4000 / 工号 4001 / 输出结果 4100 / 条码内容 4101），
/// 由 PlcTriggerService 监听触发，先写条码内容再回写结果（1=OK / 2=NG）；
/// 结果写入成功后自动生成本地记录（Data\YYYYMMDD.txt：编号,工号,时间,条码,结果），
/// 记录先落盘（Task.Run 异步执行，不阻塞 500ms 轮询与 UI），MES 上传在后续版本接入。
/// 扫码枪时序：发出 TRIGGER 后进入"扫码窗口"，窗口长度 = 扫码超时（ScanTimeout）；
/// 窗口到期自动收尾（超时写结果 2）并发送 RELEASE；窗口内扫码成功则写结果 1 并提前发送 RELEASE。
/// 事件在调用线程触发（串口线程/轮询线程），UI 层负责 BeginInvoke 调度。
/// </summary>
public sealed class ScannerController : IDisposable
{
    /// <summary>扫码超时非法（&lt;=0，会导致"触发即释放"）时的兜底值</summary>
    private static readonly TimeSpan FallbackScanTimeout = TimeSpan.FromSeconds(10);

    private IScannerTransport _scanner;
    private readonly ModbusTcpService _modbus;
    private readonly ScannerConfig _config;
    private readonly PlcTriggerService _trigger;
    private readonly DailyCsvStore _store;
    private readonly RecordNumberGenerator _numbers = new();
    private readonly object _stateLock = new();

    private int _barcodeSeq;
    private string _lastBarcode = "";
    private ScanState _state = ScanState.Idle;

    private CancellationTokenSource? _scanWindowCts; // 扫码窗口（触发→释放）计时
    private int _roundReleased = 1;                  // 1=本轮已发送释放命令（幂等，避免重复释放）
    private long _triggerTicks;                      // 本轮触发时刻（Environment.TickCount64）

    public ScannerController(IScannerTransport scanner, ModbusTcpService modbus, ScannerConfig config, string? dataDirectory = null)
    {
        _scanner = scanner;
        _modbus = modbus;
        _config = config;
        _trigger = new PlcTriggerService(modbus, config);
        _store = new DailyCsvStore(dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "Data"));

        // 现场配置若为 0/负值（会导致触发后立即超时并释放扫码枪），回退到默认 10s
        if (_config.ScanTimeout <= TimeSpan.Zero)
        {
            _config.ScanTimeout = FallbackScanTimeout;
        }

        _trigger.TriggerReceived += OnTriggerReceived;
        _trigger.ManualTriggered += OnManualTriggered;
        _trigger.ResultWritten += OnResultWritten;
        _scanner.BarcodeReceived += OnBarcodeReceived;
        _scanner.RawDataReceived += OnRawDataReceived;
        _scanner.ConnectionChanged += OnScannerConnectionChanged;
        _modbus.ConnectionChanged += connected => Log(connected ? LogLevel.Success : LogLevel.Warn,
            connected ? "PLC连接成功" : "PLC连接已断开");
    }

    public ScanState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event Action<LogEntry>? Logged;
    public event Action<string>? BarcodeScanned;
    public event Action<ScanState>? StateChanged;

    // ================= 连接管理 =================

    /// <summary>切换扫码枪通讯通道（串口 ↔ TCP）；自动卸载旧实例事件并释放</summary>
    public void SetScanner(IScannerTransport transport)
    {
        if (ReferenceEquals(_scanner, transport))
        {
            return;
        }

        _scanner.BarcodeReceived -= OnBarcodeReceived;
        _scanner.RawDataReceived -= OnRawDataReceived;
        _scanner.ConnectionChanged -= OnScannerConnectionChanged;
        _scanner.Dispose();
        _scanner = transport;
        _scanner.BarcodeReceived += OnBarcodeReceived;
        _scanner.RawDataReceived += OnRawDataReceived;
        _scanner.ConnectionChanged += OnScannerConnectionChanged;
    }

    private void OnScannerConnectionChanged(bool open)
    {
        Log(open ? LogLevel.Success : LogLevel.Warn,
            _config.TransportMode == ScannerTransportMode.Tcp
                ? (open ? "扫码枪已连接" : "扫码枪连接已断开")
                : (open ? "串口已打开" : "串口已关闭"));
    }

    public void OpenScanner() => _scanner.Open(_config);

    public void CloseScanner()
    {
        SendReleaseCommand(); // 断开前先释放扫码枪
        _scanner.Close();
    }

    public void ConnectPlc() => _modbus.Connect(_config, TimeSpan.FromSeconds(3));

    public void DisconnectPlc()
    {
        Stop();
        _modbus.Disconnect();
    }

    // ================= 扫码控制（开始/停止 = 监听 PLC 触发） =================

    /// <summary>开始扫码：启动 PLC 触发监听（不直接写寄存器，等待 PLC 侧触发）</summary>
    public void Start()
    {
        if (!_scanner.IsOpen)
        {
            throw new InvalidOperationException("扫码枪未连接，无法开始扫码");
        }

        if (!_modbus.IsConnected)
        {
            throw new InvalidOperationException("PLC 未连接，无法开始扫码");
        }

        // 只进入监听，不主动触发扫码：先把启动前遗留的触发信号（寄存器已是 1）清零，等 PLC 重新触发
        bool staleCleared = _trigger.ClearStaleTrigger();
        _trigger.Start();
        SetState(ScanState.Listening);

        if (staleCleared)
        {
            Log(LogLevel.Warn,
                $"已开始扫码（监听触发寄存器 {_config.InputTriggerRegister}）：启动时检测到残留触发信号，已清零应答且未触发扫码，等待新的 PLC 触发...");
        }
        else
        {
            Log(LogLevel.Info,
                $"已开始扫码（监听触发寄存器 {_config.InputTriggerRegister}，扫码超时 {_config.ScanTimeout.TotalSeconds:F1}s = 触发后保持扫码的时间，等待 PLC 触发...）");
        }
    }

    /// <summary>停止扫码：停止监听 PLC 触发</summary>
    public void Stop()
    {
        if (State == ScanState.Idle)
        {
            return;
        }

        _trigger.Stop();
        SendReleaseCommand(); // 停止监听时释放扫码枪
        SetState(ScanState.Idle);
        Log(LogLevel.Info, "已停止扫码（停止监听 PLC 触发）");
    }

    /// <summary>重置扫码序号与记录编号（"清除信息"按钮触发；软件重启后同样重新编号）</summary>
    public void ResetRecordNumber()
    {
        Interlocked.Exchange(ref _barcodeSeq, 0);
        _numbers.Reset();
    }

    /// <summary>手动扫码（仅管理员按钮）：直接进入等待扫码状态，等价于一次 PLC 触发。</summary>
    public void ManualScan()
    {
        if (State != ScanState.Listening)
        {
            throw new InvalidOperationException("请先开始扫码（监听 PLC 触发）后再手动扫码");
        }

        if (!_scanner.IsOpen)
        {
            throw new InvalidOperationException("扫码枪未连接，无法手动扫码");
        }

        _trigger.ManualTrigger();
    }

    // ================= PLC 触发与结果回写 =================

    private void OnTriggerReceived()
    {
        _lastBarcode = ""; // 新一轮开始，清空上一轮条码，避免超时记录沿用旧条码
        SetState(ScanState.WaitingBarcode);
        Log(LogLevel.Info, $"收到 PLC 触发（寄存器 {_config.InputTriggerRegister}=1），已清零应答，等待扫码数据（超时 {_config.ScanTimeout.TotalSeconds:F1}s）...");
        SendTriggerCommand();
    }

    private void OnManualTriggered()
    {
        _lastBarcode = "";
        SetState(ScanState.WaitingBarcode);
        Log(LogLevel.Info, $"手动扫码：等待扫码数据（超时 {_config.ScanTimeout.TotalSeconds:F1}s）...");
        SendTriggerCommand();
    }

    private void OnResultWritten(ushort result)
    {
        SetState(ScanState.Listening); // 回到监听态，等待下一轮触发
        string resultText = result == PlcTriggerService.ResultOk ? "OK" : "NG";
        Log(result == PlcTriggerService.ResultOk ? LogLevel.Success : LogLevel.Warn,
            $"数据写入成功（结果寄存器 {_config.OutputResultRegister}={result}，结果 {resultText}）");

        // 结果已确定（1=OK / 2=NG）：生成本地记录并释放扫码枪
        // 扫码成功 → 提前释放；超时 → 窗口到期释放（触发→释放间隔 = 扫码超时）
        SaveRecord(resultText);
        ReleaseScanner(result == PlcTriggerService.ResultOk ? "扫码成功" : "扫码超时");
    }

    // ================= 条码处理（显示 + 写入条码内容寄存器） =================

    private void OnBarcodeReceived(string barcode)
    {
        // 部分固件收到命令后会回显 TRIGGER/RELEASE：不是扫码数据，必须忽略，
        // 否则会被当成扫码成功，导致"触发一下就释放"
        if (IsCommandEcho(barcode))
        {
            Log(LogLevel.Info, $"忽略扫码枪命令回显（非扫码数据）: {barcode}");
            return;
        }

        _lastBarcode = barcode;

        // 条码始终显示在 UI（最新一条置顶，带时间与自增序号）
        int seq = Interlocked.Increment(ref _barcodeSeq);
        BarcodeScanned?.Invoke($"[{DateTime.Now:HH:mm:ss}] #{seq} {barcode}");

        if (!_trigger.IsWaiting)
        {
            Log(LogLevel.Info, $"收到条码（未在等待触发状态，仅显示）: {barcode}");
            return;
        }

        // 正处于等待扫码数据状态：先写条码内容寄存器，再回写结果 1 (OK)
        // 结果写入成功 → OnResultWritten 立即发送 RELEASE（扫码成功提前释放，不必等满超时）
        try
        {
            BarcodeWriteOutcome? outcome = _trigger.HandleBarcode(barcode);
            if (outcome is { } write)
            {
                // 条码内容写入失败（如 INT16 装不下超长条码）必须醒目报错：
                // 此时结果寄存器仍写 1(OK)，但 PLC 读不到条码内容，会被判成"扫码失败"
                Log(write.Success ? LogLevel.Success : LogLevel.Error, write.Message);
            }
        }
        catch (Exception ex)
        {
            SetState(ScanState.Listening);
            ReleaseScanner("写结果失败"); // 写结果失败也释放扫码枪，避免一直亮着
            Log(LogLevel.Error, $"指令发送失败: {ex.Message}");
        }
    }

    /// <summary>是否为扫码枪命令回显（TRIGGER / RELEASE，忽略大小写与空白）</summary>
    private static bool IsCommandEcho(string frame)
    {
        string text = frame.Trim();
        return text.Equals(ScannerCommand.Trigger, StringComparison.OrdinalIgnoreCase)
            || text.Equals(ScannerCommand.Release, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 扫码枪原始字节上报（现场排查数据通道）：以 HEX 形式记录，
    /// 只要日志里出现"扫码枪原始数据 RX"，就说明 TCP/串口通路已收到扫码枪数据。
    /// </summary>
    private void OnRawDataReceived(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return;
        }

        Log(LogLevel.Info, $"扫码枪原始数据 RX（{raw.Length} 字符）HEX: {ToHex(raw)}");
    }

    /// <summary>把文本转成 HEX（最多 64 字节），便于看清 STX/ETX/CR/LF 等不可见字符</summary>
    private static string ToHex(string text, int maxBytes = 64)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        int count = Math.Min(bytes.Length, maxBytes);
        var sb = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(bytes[i].ToString("X2"));
        }

        if (bytes.Length > count)
        {
            sb.Append(" ...");
        }

        return sb.ToString();
    }

    // ================= 本地记录（先落盘，MES 后续接入） =================

    /// <summary>生成记录并异步写入当天 TXT（Task.Run 落盘，不阻塞轮询与 UI）</summary>
    private void SaveRecord(string resultText)
    {
        var record = new ScanRecord(
            _numbers.Next(),
            ResolveWorkerId(),
            DateTime.Now,
            _lastBarcode,
            resultText);

        Task.Run(() =>
        {
            try
            {
                _store.Append(record, record.Timestamp);
                Log(LogLevel.Success, $"记录已保存: {record.ToCsvLine()}");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"记录保存失败: {ex.Message}");
            }
        });
    }

    /// <summary>工号来源：PLC 工号寄存器（4001）有效值优先，否则回退界面工号框（config.DefaultWorkerId）</summary>
    private string ResolveWorkerId()
    {
        ushort plcWorker = _trigger.PlcWorkerId;
        if (plcWorker > 0)
        {
            return plcWorker.ToString("D4");
        }

        return string.IsNullOrWhiteSpace(_config.DefaultWorkerId) ? "0001" : _config.DefaultWorkerId.Trim();
    }

    private void SetState(ScanState state)
    {
        lock (_stateLock)
        {
            _state = state;
        }

        StateChanged?.Invoke(state);
    }

    private void Log(LogLevel level, string message)
    {
        Logged?.Invoke(new LogEntry(DateTime.Now, level, message));
    }

    // ================= 扫码枪命令（斯普锐 C20DE-RF10）与"扫码窗口"时序 =================

    /// <summary>
    /// 发送触发命令，并按扫码超时开启"扫码窗口"：
    /// 触发后扫码枪保持工作的时间 = ScanTimeout，窗口到期自动收尾并释放；
    /// 窗口内扫码成功则立即释放（提前释放）。
    /// </summary>
    private void SendTriggerCommand()
    {
        bool ok = _scanner.SendCommand(ScannerCommand.Trigger);
        Log(ok ? LogLevel.Info : LogLevel.Warn,
            ok ? "已向扫码枪发送触发命令 TRIGGER" : "发送触发命令失败（扫码枪未连接或写入失败）");
        StartScanWindow(ok);
    }

    /// <summary>
    /// 开启本轮扫码窗口：取消上一轮未到期的计时，按 ScanTimeout 计时；
    /// 到期时若仍未拿到条码，立即按超时收尾（写结果 2）后发送 RELEASE，
    /// 保证"触发→释放"间隔与超时设定一致（不再依赖 500ms 轮询节拍，也不会触发后立即释放）。
    /// </summary>
    private void StartScanWindow(bool triggerSent)
    {
        CancelScanWindow(); // 取消上一轮未到期的计时

        Interlocked.Exchange(ref _roundReleased, 0);
        Interlocked.Exchange(ref _triggerTicks, Environment.TickCount64);

        if (!triggerSent)
        {
            // 触发命令未发到扫码枪（未连接/写入失败）：没有"保持"动作，无需释放
            Interlocked.Exchange(ref _roundReleased, 1);
            return;
        }

        TimeSpan window = _config.ScanTimeout;
        var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        Interlocked.Exchange(ref _scanWindowCts, cts);
        Log(LogLevel.Info,
            $"扫码窗口开始：扫码枪保持 {window.TotalSeconds:F1}s（= 扫码超时），超时自动释放；扫码成功则提前释放");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(window, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // 已提前释放/停止：取消计时
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            _trigger.ForceTimeout(); // 仍在等待扫码数据 → 立即写结果 2（NG）
            ReleaseScanner("扫码超时");
        });
    }

    /// <summary>本轮释放扫码枪（幂等：超时收尾与结果回写可能同时到达，只发一次 RELEASE）</summary>
    private void ReleaseScanner(string reason)
    {
        if (Interlocked.Exchange(ref _roundReleased, 1) == 1)
        {
            return; // 本轮已释放过
        }

        CancelScanWindow();

        long ticks = Interlocked.Read(ref _triggerTicks);
        string elapsed = ticks > 0
            ? $"，触发→释放 {TimeSpan.FromMilliseconds(Environment.TickCount64 - ticks).TotalSeconds:F1}s"
            : "";

        if (_scanner.SendCommand(ScannerCommand.Release))
        {
            Log(LogLevel.Info, $"{reason}：已发送释放命令 RELEASE{elapsed}");
        }
        else
        {
            Log(LogLevel.Warn, $"{reason}：发送释放命令失败（扫码枪未连接或写入失败）");
        }
    }

    /// <summary>强制释放（停止扫码/断开连接）：撤销本轮窗口计时并不论是否已释放都重发一次 RELEASE</summary>
    private void SendReleaseCommand()
    {
        CancelScanWindow();
        Interlocked.Exchange(ref _roundReleased, 1);

        if (!_scanner.SendCommand(ScannerCommand.Release))
        {
            Log(LogLevel.Warn, "发送释放命令失败（扫码枪未连接或写入失败）");
        }
    }

    /// <summary>
    /// 退出软件时调用：撤销扫码窗口计时并立即发送一次 RELEASE（不等线程结束，避免界面卡顿）。
    /// 避免"软件关闭时扫码枪仍处于触发状态、灯一直亮着"。
    /// </summary>
    public void ReleaseOnExit()
    {
        CancelScanWindow();
        Interlocked.Exchange(ref _roundReleased, 1);

        try
        {
            _scanner.SendCommand(ScannerCommand.Release);
        }
        catch (Exception)
        {
            // 退出路径不记录日志，释放失败也不影响关闭流程
        }
    }

    /// <summary>撤销本轮扫码窗口计时（不发送释放命令）</summary>
    private void CancelScanWindow()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _scanWindowCts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    public void Dispose()
    {
        _scanner.BarcodeReceived -= OnBarcodeReceived;
        _scanner.RawDataReceived -= OnRawDataReceived;
        _scanner.ConnectionChanged -= OnScannerConnectionChanged;
        _trigger.Dispose();
        Stop();
        CancelScanWindow();
        _scanner.Dispose();
        _modbus.Dispose();
    }
}
