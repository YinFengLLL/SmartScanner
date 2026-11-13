using System.Text;
using SmartScanner.Core.Models;
using SmartScanner.Core.Modbus;

namespace SmartScanner.Core.Handshake;

/// <summary>条码内容写入结果（Success=false 时 Message 为告警原因）</summary>
public readonly record struct BarcodeWriteOutcome(bool Success, string Message);

/// <summary>
/// PLC 触发扫码监听服务（新需求模型，替代旧的"写启动位→读确认位"握手）：
/// 轮询"输入触发寄存器"（默认 4000）：
///   读到 1 → 清零应答（写 0）→ 进入"等待扫码数据"状态；
///   在扫码超时内收到条码（HandleBarcode）→ 先按配置方式写"条码内容寄存器"（默认 4101），
///   再写"输出结果寄存器"（默认 4100）= 1 (OK)；
///   超时未收到 → 写 = 2（超时失败），条码内容寄存器保持不变。
/// 结果（1/2）写入成功后进入"等待触发复位"：PLC 保持 4000=1 时不再重复触发/重复写结果，
/// 直到读到 4000=0 才允许下一轮（结果只写一次，避免 PLC 未复位时反复写 2）。
/// 超时既由轮询节拍检查（按剩余时间精确唤醒），也可由控制器扫码窗口到期时调用
/// <see cref="ForceTimeout"/> 立即收尾，保证"触发→写结果/释放扫码枪"的间隔与扫码超时一致。
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

    /// <summary>
    /// 开始监听前清除"残留触发"：若触发寄存器当前已经是 1（上次运行遗留的电平/开机前已置位），
    /// 只写 0 应答、**不触发扫码**，之后的轮询等待 PLC 重新给出触发信号。
    /// 避免"按下开始扫码 / 开机自动启动后立刻触发一次扫码"。
    /// </summary>
    /// <returns>true=检测并清除了残留触发信号；false=寄存器本来就是 0 或读取失败</returns>
    public bool ClearStaleTrigger()
    {
        try
        {
            ushort value = _modbus.ReadHoldingRegisters(_config.SlaveId, _config.InputTriggerRegister, 1)[0];
            if (value != 1)
            {
                return false;
            }

            _modbus.WriteSingleRegister(_config.SlaveId, _config.InputTriggerRegister, 0);
            return true;
        }
        catch (Exception)
        {
            return false; // PLC 未就绪（读失败）：交由轮询线程按正常流程处理
        }
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

    /// <summary>
    /// 由控制器在收到条码时调用：若正处于等待状态，先写"条码内容寄存器"，
    /// 再写结果寄存器 1（OK）并复位（线程安全）。
    /// 先写条码、后写结果，保证 PLC 读到 OK 时条码内容已就绪。
    /// </summary>
    /// <param name="barcode">扫码枪返回的原始条码；为空则只写结果（兼容旧调用）</param>
    /// <returns>条码写入结果；未处于等待状态时返回 null（不写入任何寄存器）</returns>
    public BarcodeWriteOutcome? HandleBarcode(string? barcode = null)
    {
        if (Interlocked.CompareExchange(ref _state, StateIdle, StateWaiting) != StateWaiting)
        {
            return null; // 未在等待状态（或已被超时抢占），不写入
        }

        BarcodeWriteOutcome? outcome = null;
        if (!string.IsNullOrWhiteSpace(barcode))
        {
            outcome = WriteBarcodeContent(barcode.Trim());
        }

        _modbus.WriteSingleRegister(_config.SlaveId, _config.OutputResultRegister, ResultOk);
        _awaitingReset = true; // 结果已写一次，等待 PLC 复位后再响应新触发
        ResultWritten?.Invoke(ResultOk);
        return outcome;
    }

    /// <summary>
    /// 扫码窗口到期时由控制器调用：若仍在等待扫码数据，立即按超时收尾
    /// （写结果寄存器 2 并触发 <see cref="ResultWritten"/>），
    /// 避免依赖轮询节拍（PollInterval）导致超时结果与释放命令滞后。
    /// </summary>
    /// <returns>true=本轮由本次调用收尾；false=本轮已被扫码成功/已超时处理，无需重复写入</returns>
    public bool ForceTimeout()
    {
        if (Interlocked.CompareExchange(ref _state, StateIdle, StateWaiting) != StateWaiting)
        {
            return false;
        }

        WriteResult(ResultTimeout);
        return true;
    }

    /// <summary>按配置的写入方式（INT16 / INT32 / STRING）把条码内容写入保持寄存器</summary>
    private BarcodeWriteOutcome WriteBarcodeContent(string barcode)
    {
        ushort address = _config.BarcodeRegister;

        try
        {
            switch (_config.BarcodeWriteMode)
            {
                case BarcodeWriteMode.Int32:
                {
                    if (!ulong.TryParse(barcode, out ulong value) || value > uint.MaxValue)
                    {
                        return new BarcodeWriteOutcome(false,
                            $"条码内容无法按 INT32 写入寄存器 {address}（需 0~4294967295 的纯数字）: {barcode}");
                    }

                    // 32 位无符号：高字在前、低字在后（2 个连续寄存器）
                    var registers = new[] { (ushort)(value >> 16), (ushort)(value & 0xFFFF) };
                    _modbus.WriteMultipleRegisters(_config.SlaveId, address, registers);
                    return new BarcodeWriteOutcome(true,
                        $"条码内容已写入寄存器 {address}~{address + 1}（INT32，高字在前）= {value}，原条码 {barcode}");
                }

                case BarcodeWriteMode.String:
                {
                    int registerCount = Math.Max(1, (int)_config.BarcodeStringRegisterCount);
                    int capacity = registerCount * 2;
                    string text = barcode.Length > capacity ? barcode[..capacity] : barcode;
                    bool truncated = text.Length < barcode.Length;

                    byte[] bytes = Encoding.ASCII.GetBytes(text); // 每寄存器 2 字符，高字节在前
                    var registers = new ushort[registerCount];
                    for (int i = 0; i < registerCount; i++)
                    {
                        byte high = i * 2 < bytes.Length ? bytes[i * 2] : (byte)0;
                        byte low = i * 2 + 1 < bytes.Length ? bytes[i * 2 + 1] : (byte)0;
                        registers[i] = (ushort)((high << 8) | low);
                    }

                    _modbus.WriteMultipleRegisters(_config.SlaveId, address, registers);
                    string suffix = truncated ? $"，超出 {capacity} 字符已截断" : "";
                    return new BarcodeWriteOutcome(!truncated,
                        $"条码内容已写入寄存器 {address}~{address + registerCount - 1}（STRING，ASCII 高字节在前）= {text}{suffix}");
                }

                default: // Int16
                {
                    if (!ulong.TryParse(barcode, out ulong value) || value > ushort.MaxValue)
                    {
                        return new BarcodeWriteOutcome(false,
                            $"条码内容无法按 INT16 写入寄存器 {address}（需 0~65535 的纯数字，超范围请改用 INT32/STRING）: {barcode}");
                    }

                    ushort register = (ushort)value;
                    _modbus.WriteSingleRegister(_config.SlaveId, address, register);
                    return new BarcodeWriteOutcome(true,
                        $"条码内容已写入寄存器 {address}（INT16）= {register}，原条码 {barcode}");
                }
            }
        }
        catch (Exception ex)
        {
            return new BarcodeWriteOutcome(false, $"条码内容写入寄存器 {address} 失败: {ex.Message}");
        }
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

            // 等待下一轮轮询；等待扫码数据时按"剩余等待时间"精确唤醒，
            // 使超时结果尽量贴近触发 + ScanTimeout，而不是被轮询节拍拖后
            if (ct.WaitHandle.WaitOne(NextWaitMilliseconds()))
            {
                break;
            }
        }
    }

    /// <summary>下一次轮询的等待毫秒数：等待扫码数据时取 min(轮询间隔, 剩余超时时间)</summary>
    private int NextWaitMilliseconds()
    {
        int waitMs = (int)Math.Max(1, _config.PollInterval.TotalMilliseconds);
        if (Volatile.Read(ref _state) != StateWaiting)
        {
            return waitMs;
        }

        double remainingMs = (_waitDeadline - DateTime.Now).TotalMilliseconds;
        if (remainingMs < waitMs)
        {
            waitMs = (int)Math.Max(1, Math.Ceiling(remainingMs));
        }

        return waitMs;
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
