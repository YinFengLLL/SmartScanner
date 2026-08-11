namespace SmartScanner.Core.Serial;

/// <summary>
/// 斯普锐 C20DE-RF10 扫码枪命令常量（用户确认：命令本身不含分号，分号仅作格式分隔说明）：
/// 触发命令 TRIGGER（HEX 54524947474552），释放命令 RELEASE（HEX 52454C45415345）。
/// </summary>
public static class ScannerCommand
{
    /// <summary>触发命令：让扫码枪开始扫描</summary>
    public const string Trigger = "TRIGGER";

    /// <summary>释放命令：让扫码枪停止扫描</summary>
    public const string Release = "RELEASE";
}
