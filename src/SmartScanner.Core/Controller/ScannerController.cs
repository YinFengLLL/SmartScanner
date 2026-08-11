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
/// 串口接收的条码仅显示在 UI（不写入 PLC）；
/// PLC 交互仅通过保持寄存器（输入触发 4000 / 工号 4001 / 输出结果 4100 / 输出备用 4101），
/// 由 PlcTriggerService 监听触发并回写结果（1=OK / 2=NG）；
/// 结果写入成功后自动生成本地记录（Data\YYYYMMDD.txt：编号,工号,时间,条码,结果），
/// 记录先落盘（Task.Run 异步执行，不阻塞 500ms 轮询与 UI），MES 上传在后续版本接入。
/// 事件在调用线程触发（串口线程/轮询线程），UI 层负责 BeginInvoke 调度。
/// </summary>
public sealed class ScannerController : IDisposable
{
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

    public ScannerController(IScannerTransport scanner, ModbusTcpService modbus, ScannerConfig config, string? dataDirectory = null)
    {
        _scanner = scanner;
        _modbus = modbus;
        _config = config;
        _trigger = new PlcTriggerService(modbus, config);
        _store = new DailyCsvStore(dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "Data"));

        _trigger.TriggerReceived += OnTriggerReceived;
        _trigger.ManualTriggered += OnManualTriggered;
        _trigger.ResultWritten += OnResultWritten;
        _scanner.BarcodeReceived += OnBarcodeReceived;
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
        _scanner.ConnectionChanged -= OnScannerConnectionChanged;
        _scanner.Dispose();
        _scanner = transport;
        _scanner.BarcodeReceived += OnBarcodeReceived;
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

        _trigger.Start();
        SetState(ScanState.Listening);
        Log(LogLevel.Info, $"已开始扫码（监听触发寄存器 {_config.InputTriggerRegister}，等待 PLC 触发...）");
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
        SetState(ScanState.WaitingBarcode);
        Log(LogLevel.Info, $"收到 PLC 触发（寄存器 {_config.InputTriggerRegister}=1），已清零应答，等待扫码数据（超时 {_config.ScanTimeout.TotalSeconds:F0}s）...");
        SendTriggerCommand();
    }

    private void OnManualTriggered()
    {
        SetState(ScanState.WaitingBarcode);
        Log(LogLevel.Info, $"手动扫码：等待扫码数据（超时 {_config.ScanTimeout.TotalSeconds:F0}s）...");
        SendTriggerCommand();
    }

    private void OnResultWritten(ushort result)
    {
        SetState(ScanState.Listening); // 回到监听态，等待下一轮触发
        string resultText = result == PlcTriggerService.ResultOk ? "OK" : "NG";
        Log(result == PlcTriggerService.ResultOk ? LogLevel.Success : LogLevel.Warn,
            $"数据写入成功（结果寄存器 {_config.OutputResultRegister}={result}，结果 {resultText}）");

        // 结果已确定（1=OK / 2=NG），生成本地记录并释放扫码枪
        SaveRecord(resultText);
        SendReleaseCommand();
    }

    // ================= 条码处理（仅显示，不写入 PLC） =================

    private void OnBarcodeReceived(string barcode)
    {
        _lastBarcode = barcode;

        // 条码始终显示在 UI（最新一条置顶，带时间与自增序号）
        int seq = Interlocked.Increment(ref _barcodeSeq);
        BarcodeScanned?.Invoke($"[{DateTime.Now:HH:mm:ss}] #{seq} {barcode}");

        if (!_trigger.IsWaiting)
        {
            Log(LogLevel.Info, $"收到条码（未在等待触发状态，仅显示）: {barcode}");
            return;
        }

        // 正处于等待扫码数据状态：回写结果 1 (OK)
        try
        {
            _trigger.HandleBarcode();
        }
        catch (Exception ex)
        {
            SetState(ScanState.Listening);
            SendReleaseCommand(); // 写结果失败也释放扫码枪，避免一直亮着
            Log(LogLevel.Error, $"指令发送失败: {ex.Message}");
        }
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

    // ================= 扫码枪命令（斯普锐 C20DE-RF10） =================

    private void SendTriggerCommand()
    {
        bool ok = _scanner.SendCommand(ScannerCommand.Trigger);
        Log(ok ? LogLevel.Info : LogLevel.Warn,
            ok ? "已向扫码枪发送触发命令 TRIGGER" : "发送触发命令失败（扫码枪未连接或写入失败）");
    }

    private void SendReleaseCommand()
    {
        if (!_scanner.SendCommand(ScannerCommand.Release))
        {
            Log(LogLevel.Warn, "发送释放命令失败（扫码枪未连接或写入失败）");
        }
    }

    public void Dispose()
    {
        _scanner.BarcodeReceived -= OnBarcodeReceived;
        _scanner.ConnectionChanged -= OnScannerConnectionChanged;
        _trigger.Dispose();
        Stop();
        _scanner.Dispose();
        _modbus.Dispose();
    }
}
