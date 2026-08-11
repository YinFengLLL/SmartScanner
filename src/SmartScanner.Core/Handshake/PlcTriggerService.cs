using SmartScanner.Core.Models;
using SmartScanner.Core.Modbus;

namespace SmartScanner.Core.Handshake;

/// <summary>
/// PLC 触发扫码监听服务（新需求模型，替代旧的"写启动位→读确认位"握手）：
/// 轮询"输入触发寄存器"（默认 4000）：
///   读到 1 → 清零应答（写 0）→ 进入"等待扫码数据"状态；
///   在扫码超时内收到条码（HandleBarcode）→ 写"输出结果寄存器"（默认 4100）= 1 (OK)；
///   超时未收到 → 写 = 2（超时失败）。
/// 结果（1/2）写入成功后进入"等待触发复位"：PLC 保持 4000=1 时不再重复触发/重复写结果，
/// 直到读到 4000=0 才允许下一轮（结果只写一次，避免 PLC 未复位时反复写 2）。
/// 线程安全："应答权"用 Interlocked 原子抢占，条码与超时二者只允许一个写入结果寄存器。
/// </summary>
public sealed class PlcTriggerService : IDisposable
{
    /// <summary>结果寄存器值：OK（已获取扫码数据）</summary>
    public const ushort ResultOk = 1;

    /// <summary>结果寄存器值：超时（未在时限内获取扫码数据）</summary>
    public const ushort ResultTimeout = 2;

    private const int StateIdle = 0;
    private const int StateWaiting = 1;

    private readonly ModbusTcpService _modbus;
    private readonly ScannerConfig _config;

    private int _state = StateIdle; // 0=空闲，1=等待扫码数据
    private DateTime _waitDeadline;
    private CancellationTokenSource? _cts;
    private Thread? _pollThread;
    private volatile ushort _plcWorkerId; // PLC 工号寄存器（4001）最近读取值
    private volatile bool _awaitingReset; // 结果已写入，等待 PLC 复位触发寄存器后再允许新一轮

    /// <summary>收到 PLC 触发并已完成清零应答（进入等待扫码状态）</summary>
    public event Action? TriggerReceived;

    /// <summary>手动触发（管理员"手动扫码"按钮）：等价于一次 PLC 触发，进入等待扫码状态</summary>
    public event Action? ManualTriggered;

    /// <summary>结果寄存器写入完成（参数为 1=OK 或 2=超时）</summary>
    public event Action<ushort>? ResultWritten;

    public bool IsListening => _cts is not null;

    public bool IsWaiting => Volatile.Read(ref _state) == StateWaiting;

    /// <summary>PLC 工号寄存器（4001）最近读取的有效值（&gt;0）；0 表示 PLC 未提供工号</summary>
    public ushort PlcWorkerId => _plcWorkerId;

    public PlcTriggerService(ModbusTcpService modbus, ScannerConfig config)
    {
        _modbus = modbus;
        _config = config;
    }

    /// <summary>启动 PLC 触发监听（后台轮询线程）</summary>
    public void Start()
    {
        if (IsListening)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _pollThread = new Thread(() => PollLoop(cts.Token))
        {
            IsBackground = true,
            Name = "PlcTriggerPoll",
        };
        _pollThread.Start();
    }

    /// <summary>停止监听；手动停止不写超时结果（不视为扫码超时）</summary>
    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        _pollThread?.Join(TimeSpan.FromSeconds(2));
        _pollThread = null;
        cts.Dispose();

        Interlocked.Exchange(ref _state, StateIdle);
        _awaitingReset = false;
    }

    /// <summary>手动触发一次扫码（管理员"手动扫码"按钮）：等价于 PLC 触发，不读写触发寄存器。
    /// 要求当前处于监听状态且未在等待；之后条码/超时处理与 PLC 触发完全一致。</summary>
    public void ManualTrigger()
    {
        if (Volatile.Read(ref _state) != StateIdle)
        {
            return; // 已在等待扫码，忽略重复触发
        }

        _waitDeadline = DateTime.Now + _config.ScanTimeout;
        Volatile.Write(ref _state, StateWaiting);
        ManualTriggered?.Invoke();
    }

    /// <summary>由控制器在收到条码时调用：若正处于等待状态则写结果 1 并复位（线程安全）</summary>
    public void HandleBarcode()
    {
        if (Interlocked.CompareExchange(ref _state, StateIdle, StateWaiting) != StateWaiting)
        {
            return; // 未在等待状态（或已被超时抢占），不写入
        }

        _modbus.WriteSingleRegister(_config.SlaveId, _config.OutputResultRegister, ResultOk);
        _awaitingReset = true; // 结果已写一次，等待 PLC 复位后再响应新触发
        ResultWritten?.Invoke(ResultOk);
    }

    private void PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Volatile.Read(ref _state) == StateWaiting)
            {
                // 等待扫码数据：检查是否超时
                if (DateTime.Now >= _waitDeadline)
                {
                    if (Interlocked.CompareExchange(ref _state, StateIdle, StateWaiting) == StateWaiting)
                    {
                        WriteResult(ResultTimeout);
                    }
                }
            }
            else
            {
                TryPollTrigger(ct);
            }

            if (ct.WaitHandle.WaitOne(_config.PollInterval))
            {
                break;
            }
        }
    }

    private void TryPollTrigger(CancellationToken ct)
    {
        try
        {
            // 顺带读取工号寄存器（默认 4001）：值 > 0 时更新当前工号
            ushort workerId = _modbus.ReadHoldingRegisters(_config.SlaveId, _config.WorkerIdRegister, 1)[0];
            if (workerId > 0)
            {
                _plcWorkerId = workerId;
            }

            // 结果已写入（1/2）：抑制重复触发，直到 PLC 将触发寄存器复位为 0
            if (_awaitingReset)
            {
                ushort resetValue = _modbus.ReadHoldingRegisters(_config.SlaveId, _config.InputTriggerRegister, 1)[0];
                if (resetValue == 0)
                {
                    _awaitingReset = false; // 解除抑制，允许下一轮触发
                }

                return;
            }

            ushort value = _modbus.ReadHoldingRegisters(_config.SlaveId, _config.InputTriggerRegister, 1)[0];
            if (value != 1)
            {
                return;
            }

            // 清零应答（写 0），随后进入等待扫码状态
            _modbus.WriteSingleRegister(_config.SlaveId, _config.InputTriggerRegister, 0);
            _waitDeadline = DateTime.Now + _config.ScanTimeout;
            Volatile.Write(ref _state, StateWaiting);
            TriggerReceived?.Invoke();
        }
        catch (Exception)
        {
            // 读失败（如 PLC 断线）：忽略本轮，下轮重试；连接状态由上层日志体现
        }
    }

    private void WriteResult(ushort result)
    {
        try
        {
            _modbus.WriteSingleRegister(_config.SlaveId, _config.OutputResultRegister, result);
            _awaitingReset = true; // 结果已写一次，等待 PLC 复位后再响应新触发
            ResultWritten?.Invoke(result);
        }
        catch (Exception)
        {
            // 写失败由 Controller 统一记录日志（通过 ResultWritten 无法触发时的处理见调用方）
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
