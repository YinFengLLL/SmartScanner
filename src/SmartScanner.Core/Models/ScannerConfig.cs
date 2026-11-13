using System.IO.Ports;

namespace SmartScanner.Core.Models;

/// <summary>
/// 系统运行参数配置：串口、PLC、寄存器地址映射与触发监听时序参数。
/// 所有字段带工业默认值，管理员模式下可编辑。
/// </summary>
public sealed class ScannerConfig
{
    // ================= 串口参数 =================
    public string ComPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.None;

    // ================= PLC 参数 =================
    public string PlcIp { get; set; } = "192.168.1.100";
    public int PlcPort { get; set; } = 502;
    public byte SlaveId { get; set; } = 1;

    // ================= 寄存器地址映射（4 个独立保持寄存器，管理员可改） =================

    /// <summary>输入触发寄存器：PLC 写 1 触发扫码；上位机读到 1 后写 0 清零应答</summary>
    public ushort InputTriggerRegister { get; set; } = 4000;

    /// <summary>工号寄存器：PLC 写入当前工号值，上位机轮询读取确定工号（读 0 时回退到界面工号框）</summary>
    public ushort WorkerIdRegister { get; set; } = 4001;

    /// <summary>输出结果寄存器：上位机写 1 = OK（已获取扫码数据）/ 2 = NG（未获取/超时）</summary>
    public ushort OutputResultRegister { get; set; } = 4100;

    /// <summary>条码内容寄存器（原"输出备用"）：扫码 OK 后，把条码内容按 <see cref="BarcodeWriteMode"/> 写入</summary>
    public ushort BarcodeRegister { get; set; } = 4101;

    /// <summary>条码内容写入方式（默认 INT16 单寄存器；条码数值超过 65535 时改用 INT32）</summary>
    public BarcodeWriteMode BarcodeWriteMode { get; set; } = BarcodeWriteMode.Int16;

    /// <summary>STRING 模式下占用的连续寄存器数量（每寄存器 2 个 ASCII 字符，默认 6 个 = 12 字符）</summary>
    public ushort BarcodeStringRegisterCount { get; set; } = 6;

    /// <summary>旧版配置兼容：原"输出备用寄存器"字段，读取旧 config.json 时自动映射到 <see cref="BarcodeRegister"/>（不再写出）</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ushort? OutputSpareRegister
    {
        get => null;
        set
        {
            if (value.HasValue)
            {
                BarcodeRegister = value.Value;
            }
        }
    }

    // ================= 扫码枪通讯 =================

    /// <summary>通讯方式：串口（USB COM）或 TCP/IP CLIENT</summary>
    public ScannerTransportMode TransportMode { get; set; } = ScannerTransportMode.Tcp;

    /// <summary>扫码枪 TCP 目标 IP（TCP 模式；扫码枪侧需配置为 TCP Server）</summary>
    public string ScannerIp { get; set; } = "192.168.1.200";

    /// <summary>扫码枪 TCP 目标端口（TCP 模式）</summary>
    public int ScannerPort { get; set; } = 2000;

    // ================= 扫码枪数据格式（前后缀识别） =================

    /// <summary>
    /// 扫码枪返回数据的切帧方式（前后缀识别）：默认"自动识别"，
    /// 带 CR / LF / CRLF 后缀的按后缀切帧；不带前后缀的按静默时间分帧。
    /// </summary>
    public ScannerFrameMode FrameMode { get; set; } = ScannerFrameMode.Auto;

    /// <summary>
    /// 静默分帧时间（毫秒）：收到数据后静默这么久仍没有结束符，就把缓冲内容当成一条条码。
    /// 专用于"扫码枪返回信息不带前缀和后缀"的现场；有效范围 50~5000，默认 150。
    /// </summary>
    public int FrameIdleFlushMs { get; set; } = 150;

    /// <summary>是否去掉条码前后的不可见字符（STX/ETX/空格/CR/LF 等前缀、后缀），默认开启</summary>
    public bool TrimFrameAffixes { get; set; } = true;

    // ================= 工号 =================

    /// <summary>界面工号框默认值；PLC 工号寄存器读 0 或读取失败时回退使用</summary>
    public string DefaultWorkerId { get; set; } = "0001";

    // ================= 触发监听与扫码超时 =================

    /// <summary>PLC 触发寄存器轮询间隔</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>收到触发后等待扫码数据的超时时间，超时向结果寄存器写 2</summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>单次 Modbus 读写超时</summary>
    public TimeSpan ModbusTimeout { get; set; } = TimeSpan.FromMilliseconds(1000);
}
