namespace SmartScanner.Core.Models;

/// <summary>
/// 条码内容写入 PLC 保持寄存器的方式。
/// 默认 <see cref="Int16"/>：条码按十进制数值写入 1 个 16 位寄存器（0~65535）。
/// </summary>
public enum BarcodeWriteMode
{
    /// <summary>16 位有/无符号单寄存器（FC6）：条码数值 0~65535，占用 1 个寄存器。</summary>
    Int16 = 0,

    /// <summary>32 位无符号双寄存器（FC16）：条码数值 0~4294967295，高字在前，占用 2 个寄存器。</summary>
    Int32 = 1,

    /// <summary>ASCII 字符串多寄存器（FC16）：每寄存器 2 个字符（高字节在前），左侧补 0x00，寄存器数量可配。</summary>
    String = 2,
}
