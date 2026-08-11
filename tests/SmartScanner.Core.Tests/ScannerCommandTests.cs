using SmartScanner.Core.Serial;

namespace SmartScanner.Core.Tests;

public class ScannerCommandTests
{
    [Fact]
    public void 触发命令为TRIGGER不含分号()
    {
        Assert.Equal("TRIGGER", ScannerCommand.Trigger);
    }

    [Fact]
    public void 释放命令为RELEASE不含分号()
    {
        Assert.Equal("RELEASE", ScannerCommand.Release);
    }
}

public class SerialScannerServiceSendTests
{
    [Fact]
    public void 串口未打开时发送命令返回false不抛异常()
    {
        using var serial = new SerialScannerService();
        bool result = serial.SendCommand(ScannerCommand.Trigger);
        Assert.False(result); // 未打开串口应安全降级，而非抛异常
    }
}
