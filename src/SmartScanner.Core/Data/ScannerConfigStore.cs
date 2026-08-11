using System.Text.Json;
using SmartScanner.Core.Models;

namespace SmartScanner.Core.Data;

/// <summary>
/// 参数配置持久化（设置记忆）：System.Text.Json 序列化 ScannerConfig 到 JSON 文件。
/// 程序启动时 Load（文件不存在/损坏返回 null，调用方使用默认配置）；
/// 管理员修改参数后 Save（修改即存，防止崩溃/断电丢失配置）。
/// </summary>
public sealed class ScannerConfigStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public ScannerConfigStore(string filePath)
    {
        _path = filePath;
    }

    public string FilePath => _path;

    /// <summary>加载配置；文件不存在或解析失败返回 null（不阻断启动）</summary>
    public ScannerConfig? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<ScannerConfig>(json, Options);
        }
        catch (Exception)
        {
            return null; // 损坏的配置不阻断启动，回退默认值
        }
    }

    /// <summary>保存配置；写入失败返回 false（不抛异常，由调用方记录日志）</summary>
    public bool Save(ScannerConfig config)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(config, Options);
            File.WriteAllText(_path, json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
