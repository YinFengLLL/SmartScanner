namespace SmartScanner.Core.Data;

/// <summary>
/// 一条扫码记录（本地 TXT 存档的最小单元）。
/// 字段顺序与 CSV 表头一致：编号,工号,时间,条码,结果。
/// </summary>
public sealed record ScanRecord(string SerialNo, string WorkerId, DateTime Timestamp, string Barcode, string Result)
{
    /// <summary>转为 CSV 行（逗号分隔，UTF-8 落盘）</summary>
    public string ToCsvLine() =>
        $"{SerialNo},{WorkerId},{Timestamp:yyyy-MM-dd HH:mm:ss},{Barcode},{Result}";
}
