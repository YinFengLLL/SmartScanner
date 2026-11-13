namespace SmartScanner.Core.Models;

/// <summary>
/// 扫码枪返回数据的切帧（前后缀）方式：
/// 决定上位机如何判断"一条条码结束"，以及是否兼容不带前后缀的扫码枪。
/// </summary>
public enum ScannerFrameMode
{
    /// <summary>自动识别（推荐）：CR、LF、CRLF 结尾都能切帧；没有任何结尾符时按"静默时间"分帧</summary>
    Auto = 0,

    /// <summary>仅 \r\n 结尾切帧</summary>
    CrLf = 1,

    /// <summary>仅 \r 结尾切帧</summary>
    Cr = 2,

    /// <summary>仅 \n 结尾切帧</summary>
    Lf = 3,

    /// <summary>无前后缀：只按"静默时间"分帧（扫码枪返回信息不带前缀和后缀时选此项）</summary>
    None = 4,
}
