using SmartScanner.Core.Data;

namespace SmartScanner.Core.Tests;

public sealed class DailyCsvStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss_test_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void 构造时自动创建Data目录()
    {
        var store = new DailyCsvStore(_dir);
        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void 首次追加创建文件并写表头()
    {
        var store = new DailyCsvStore(_dir);
        var record = new ScanRecord("20260808-001", "0001", new DateTime(2026, 8, 8, 10, 30, 25), "ABC123", "OK");
        store.Append(record, record.Timestamp);

        string[] lines = File.ReadAllLines(store.GetFilePath(record.Timestamp));
        Assert.Equal(DailyCsvStore.Header, lines[0]);
        Assert.Equal("20260808-001,0001,2026-08-08 10:30:25,ABC123,OK", lines[1]);
    }

    [Fact]
    public void 追加多条记录不重复写表头()
    {
        var store = new DailyCsvStore(_dir);
        var t = new DateTime(2026, 8, 8, 10, 0, 0);
        store.Append(new ScanRecord("20260808-001", "0001", t, "AAA", "OK"), t);
        store.Append(new ScanRecord("20260808-002", "0001", t, "BBB", "NG"), t);

        string[] lines = File.ReadAllLines(store.GetFilePath(t));
        Assert.Equal(3, lines.Length); // 表头 + 2 条记录
        Assert.Equal("20260808-002,0001,2026-08-08 10:00:00,BBB,NG", lines[2]);
    }

    [Fact]
    public void 不同日期写入不同文件()
    {
        var store = new DailyCsvStore(_dir);
        var d1 = new DateTime(2026, 8, 8, 9, 0, 0);
        var d2 = new DateTime(2026, 8, 9, 9, 0, 0);
        store.Append(new ScanRecord("20260808-001", "0001", d1, "A", "OK"), d1);
        store.Append(new ScanRecord("20260809-001", "0001", d2, "B", "OK"), d2);

        Assert.True(File.Exists(store.GetFilePath(d1)));
        Assert.True(File.Exists(store.GetFilePath(d2)));
        Assert.NotEqual(store.GetFilePath(d1), store.GetFilePath(d2));
    }

    [Fact]
    public void 编码为UTF8且无BOM()
    {
        var store = new DailyCsvStore(_dir);
        var t = new DateTime(2026, 8, 8);
        store.Append(new ScanRecord("20260808-001", "0001", t, "ABC", "OK"), t);

        byte[] bytes = File.ReadAllBytes(store.GetFilePath(t));
        Assert.NotEqual(0xEF, bytes[0]); // 首字节非 EF BB BF，无 BOM
    }
}

public class RecordNumberGeneratorTests
{
    [Fact]
    public void 编号从001开始递增()
    {
        var gen = new RecordNumberGenerator();
        string first = gen.Next();
        string second = gen.Next();
        Assert.Equal($"{DateTime.Now:yyyyMMdd}-001", first);
        Assert.Equal($"{DateTime.Now:yyyyMMdd}-002", second);
    }

    [Fact]
    public void 重置后重新从001编号()
    {
        var gen = new RecordNumberGenerator();
        gen.Next();
        gen.Next();
        gen.Reset();
        Assert.Equal($"{DateTime.Now:yyyyMMdd}-001", gen.Next());
    }
}
