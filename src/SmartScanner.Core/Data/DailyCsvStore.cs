using System.Text;

namespace SmartScanner.Core.Data;

/// <summary>
/// 本地 TXT 存档服务：
/// 运行目录下自动创建 Data 文件夹；日志按天分割（Data\YYYYMMDD.txt）；
/// 编码 UTF-8（无 BOM），逗号分隔；文件不存在时先写表头再追加数据。
/// 线程安全：所有文件操作以 lock 串行化，使用 File.AppendAllText 追加，避免多线程争抢锁死文件。
/// </summary>
public sealed class DailyCsvStore
{
    /// <summary>表头（首行）：编号,工号,时间,条码,结果</summary>
    public const string Header = "编号,工号,时间,条码,结果";

    private readonly string _dataDirectory;
    private readonly object _sync = new();

    public DailyCsvStore(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
    }

    /// <summary>Data 文件夹完整路径</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>某天对应的文件名（Data\YYYYMMDD.txt）</summary>
    public string GetFilePath(DateTime date) => Path.Combine(_dataDirectory, date.ToString("yyyyMMdd") + ".txt");

    /// <summary>追加一条记录到当天文件；文件不存在时先写表头。线程安全。</summary>
    public void Append(ScanRecord record, DateTime now)
    {
        lock (_sync)
        {
            string path = GetFilePath(now);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, Header + Environment.NewLine, new UTF8Encoding(false));
            }

            File.AppendAllText(path, record.ToCsvLine() + Environment.NewLine, new UTF8Encoding(false));
        }
    }
}
