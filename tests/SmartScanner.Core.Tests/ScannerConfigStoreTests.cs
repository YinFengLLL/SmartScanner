using SmartScanner.Core.Data;
using SmartScanner.Core.Models;

namespace SmartScanner.Core.Tests;

public sealed class ScannerConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss_cfg_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private ScannerConfigStore NewStore() => new(Path.Combine(_dir, "config.json"));

    [Fact]
    public void 保存后重新加载_配置一致()
    {
        var config = new ScannerConfig
        {
            PlcIp = "192.168.10.50",
            PlcPort = 1502,
            ScannerIp = "10.0.0.88",
            ScannerPort = 3000,
            TransportMode = ScannerTransportMode.Serial,
            ComPort = "COM9",
            InputTriggerRegister = 4000,
            OutputResultRegister = 4100,
            ScanTimeout = TimeSpan.FromSeconds(20),
            DefaultWorkerId = "0088",
        };

        var store = NewStore();
        Assert.True(store.Save(config));

        ScannerConfig? loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(config.PlcIp, loaded!.PlcIp);
        Assert.Equal(config.PlcPort, loaded.PlcPort);
        Assert.Equal(config.ScannerIp, loaded.ScannerIp);
        Assert.Equal(config.ScannerPort, loaded.ScannerPort);
        Assert.Equal(config.TransportMode, loaded.TransportMode);
        Assert.Equal(config.ComPort, loaded.ComPort);
        Assert.Equal(config.InputTriggerRegister, loaded.InputTriggerRegister);
        Assert.Equal(config.OutputResultRegister, loaded.OutputResultRegister);
        Assert.Equal(config.ScanTimeout, loaded.ScanTimeout);
        Assert.Equal(config.DefaultWorkerId, loaded.DefaultWorkerId);
    }

    [Fact]
    public void 文件不存在时Load返回null()
    {
        Assert.Null(NewStore().Load());
    }

    [Fact]
    public void 损坏配置文件Load返回null不抛异常()
    {
        string path = Path.Combine(_dir, "config.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{ 这不是合法 JSON !!!");
        Assert.Null(NewStore().Load());
    }
}
