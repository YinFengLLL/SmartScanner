namespace SmartScanner.Core.Data;

/// <summary>
/// 记录编号生成器（需求确认后的规则）：
/// 编号 = 开始监听后生成的记录次数累加（从 1 开始），格式 yyyyMMdd-NNN（如 20260808-001）。
/// 软件关闭（进程重启）或用户点击"清除信息"按钮后 Reset()，重新从 001 编号。
/// 跨天不重置序号，仅日期前缀跟随当天。线程安全。
/// </summary>
public sealed class RecordNumberGenerator
{
    private int _count;

    /// <summary>取下一个编号（yyyyMMdd-NNN，NNN 自增补零）</summary>
    public string Next()
    {
        int n = Interlocked.Increment(ref _count);
        return $"{DateTime.Now:yyyyMMdd}-{n:D3}";
    }

    /// <summary>重置编号（清除信息按钮 / 软件重启后重新编号）</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
    }
}
