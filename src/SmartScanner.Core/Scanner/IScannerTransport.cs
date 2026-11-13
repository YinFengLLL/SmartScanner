using SmartScanner.Core.Models;

namespace SmartScanner.Core.Scanner;

/// <summary>
/// 扫码枪通讯通道抽象：串口（USB COM）与 TCP/IP CLIENT 两种实现共用同一契约。
/// 事件在接收线程触发，UI 层需自行 BeginInvoke 调度。
/// </summary>
public interface IScannerTransport : IDisposable
{
    /// <summary>是否已打开/连接</summary>
    bool IsOpen { get; }

    /// <summary>收到一条完整条码（接收线程触发）</summary>
    event Action<string>? BarcodeReceived;

    /// <summary>收到扫码枪原始字节（解码前的文本块，仅用于现场排查数据通道；接收线程触发）</summary>
    event Action<string>? RawDataReceived;

    /// <summary>连接状态变化（IsOpen）</summary>
    event Action<bool>? ConnectionChanged;

    /// <summary>打开/连接扫码枪；失败抛出异常并由调用方记录日志</summary>
    void Open(ScannerConfig config);

    /// <summary>关闭/断开</summary>
    void Close();

    /// <summary>发送 ASCII 命令（如 TRIGGER / RELEASE）；未连接或写入失败返回 false（不抛异常）</summary>
    bool SendCommand(string ascii);
}
