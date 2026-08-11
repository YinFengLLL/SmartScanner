namespace SmartScanner.Core.Events;

/// <summary>日志级别</summary>
public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error,
}

/// <summary>带时间戳的日志条目</summary>
public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public override string ToString() =>
        $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] {Message}";
}

/// <summary>扫码流程状态</summary>
public enum ScanState
{
    /// <summary>空闲（未开始监听或已停止）</summary>
    Idle,

    /// <summary>监听中：正在轮询 PLC 触发寄存器，等待 PLC 触发</summary>
    Listening,

    /// <summary>等待扫码数据：已收到 PLC 触发并清零应答，等待扫码枪条码</summary>
    WaitingBarcode,
}
