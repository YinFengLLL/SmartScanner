using System.Net;
using System.Net.Sockets;
using NModbus;
using SmartScanner.Core.Controller;
using SmartScanner.Core.Events;
using SmartScanner.Core.Handshake;
using SmartScanner.Core.Models;
using SmartScanner.Core.Modbus;
using SmartScanner.Core.Scanner;
using SmartScanner.Core.Serial;

namespace SmartScanner.Core.Tests;

/// <summary>
/// ScannerController 扫码枪"触发→释放"时序测试：
/// 触发后必须保持到扫码超时（不得触发即释放），扫码成功则提前释放。
/// 用本地 Modbus TCP 从站模拟 PLC，用假扫码枪通道记录 TRIGGER / RELEASE 命令与时刻。
/// </summary>
public sealed class ScannerControllerTimingTests : IDisposable
{
    private const ushort TriggerReg = 4000;
    private const ushort ResultReg = 4100;
    private const ushort BarcodeReg = 4101;

    private readonly ModbusFactory _factory = new();
    private readonly IModbusTcpSlaveNetwork _network;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _dataDir;
    private readonly int _port;

    public ScannerControllerTimingTests()
    {
        _port = GetFreePort();
        _network = _factory.CreateSlaveNetwork(new TcpListener(IPAddress.Loopback, _port));
        _network.AddSlave(_factory.CreateSlave(1));
        _ = Task.Run(() => _network.ListenAsync(_cts.Token));
        _dataDir = Path.Combine(Path.GetTempPath(), "SmartScannerTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _network.Dispose();
        try
        {
            if (Directory.Exists(_dataDir))
            {
                Directory.Delete(_dataDir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private ScannerConfig NewConfig(TimeSpan poll, TimeSpan timeout) => new()
    {
        PlcIp = "127.0.0.1",
        PlcPort = _port,
        SlaveId = 1,
        PollInterval = poll,
        ScanTimeout = timeout,
        ModbusTimeout = TimeSpan.FromMilliseconds(1000),
        BarcodeRegister = BarcodeReg,
        BarcodeWriteMode = BarcodeWriteMode.Int16,
    };

    private ModbusTcpService ConnectPlc(ScannerConfig config)
    {
        var modbus = new ModbusTcpService();
        modbus.Connect(config, TimeSpan.FromSeconds(3));
        return modbus;
    }

    private IModbusMaster CreatePlcSideMaster()
    {
        var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, _port);
        return _factory.CreateMaster(tcp);
    }

    [Fact]
    public void 触发后保持扫码窗口_到期才释放扫码枪()
    {
        var config = NewConfig(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(800));
        using var modbus = ConnectPlc(config);
        using var scanner = new FakeScanner();
        using var controller = new ScannerController(scanner, modbus, config, _dataDir);
        using var plcSide = CreatePlcSideMaster();

        controller.Start();
        plcSide.WriteSingleRegister(1, TriggerReg, 1);

        Assert.True(scanner.WaitForCommand(ScannerCommand.Trigger, TimeSpan.FromSeconds(5)), "未发出触发命令");
        long triggerAt = scanner.FirstTicks(ScannerCommand.Trigger);

        // 关键回归点：触发后不得立即释放（现场 bug：触发一下就释放）
        Thread.Sleep(300);
        Assert.False(scanner.HasCommand(ScannerCommand.Release), "触发后过早发送了释放命令");

        Assert.True(scanner.WaitForCommand(ScannerCommand.Release, TimeSpan.FromSeconds(5)), "超时后未发送释放命令");
        double elapsed = (scanner.LastTicks(ScannerCommand.Release) - triggerAt) / 1000.0;

        // 触发→释放间隔应与扫码超时（0.8s）一致
        Assert.InRange(elapsed, 0.6, 2.5);

        // 超时后结果寄存器写入 2（NG），且只释放一次
        Assert.Equal(PlcTriggerService.ResultTimeout, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]);
        Assert.Single(scanner.Commands, c => c.Command == ScannerCommand.Release);
    }

    [Fact]
    public void 扫码成功_提前释放并写入结果1()
    {
        var config = NewConfig(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5));
        using var modbus = ConnectPlc(config);
        using var scanner = new FakeScanner();
        using var controller = new ScannerController(scanner, modbus, config, _dataDir);
        using var plcSide = CreatePlcSideMaster();

        controller.Start();
        plcSide.WriteSingleRegister(1, TriggerReg, 1);

        Assert.True(scanner.WaitForCommand(ScannerCommand.Trigger, TimeSpan.FromSeconds(5)), "未发出触发命令");
        long triggerAt = scanner.FirstTicks(ScannerCommand.Trigger);

        scanner.EmitBarcode("00000000100"); // 11 位补零条码 → INT16 写入 100

        Assert.True(scanner.WaitForCommand(ScannerCommand.Release, TimeSpan.FromSeconds(3)), "扫码成功后未释放");
        double elapsed = (scanner.LastTicks(ScannerCommand.Release) - triggerAt) / 1000.0;

        // 提前释放：远小于 5s 的扫码超时
        Assert.InRange(elapsed, 0.0, 3.0);
        Assert.Equal(100, plcSide.ReadHoldingRegisters(1, BarcodeReg, 1)[0]);
        Assert.Equal(PlcTriggerService.ResultOk, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]);
        Assert.Single(scanner.Commands, c => c.Command == ScannerCommand.Release);
    }

    [Fact]
    public void 扫码枪命令回显_不当成扫码数据也不释放()
    {
        var config = NewConfig(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2));
        using var modbus = ConnectPlc(config);
        using var scanner = new FakeScanner();
        using var controller = new ScannerController(scanner, modbus, config, _dataDir);
        using var plcSide = CreatePlcSideMaster();

        controller.Start();
        plcSide.WriteSingleRegister(1, TriggerReg, 1);

        Assert.True(scanner.WaitForCommand(ScannerCommand.Trigger, TimeSpan.FromSeconds(5)), "未发出触发命令");

        scanner.EmitBarcode("TRIGGER"); // 命令回显，不是条码
        scanner.EmitBarcode("RELEASE");
        Thread.Sleep(300);

        Assert.False(scanner.HasCommand(ScannerCommand.Release), "命令回显被误判为扫码成功，导致提前释放");
        Assert.Equal(0, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]); // 结果寄存器仍未被写
    }

    [Fact]
    public void 开始扫码只进入监听_不主动触发扫码()
    {
        var config = NewConfig(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(800));
        using var modbus = ConnectPlc(config);
        using var scanner = new FakeScanner();
        using var controller = new ScannerController(scanner, modbus, config, _dataDir);
        using var plcSide = CreatePlcSideMaster();

        // 启动前触发寄存器残留为 1（上次运行遗留）
        plcSide.WriteSingleRegister(1, TriggerReg, 1);

        controller.Start();
        Thread.Sleep(400);

        Assert.False(scanner.HasCommand(ScannerCommand.Trigger), "按下开始扫码后直接触发了一次扫码");
        Assert.Equal(ScanState.Listening, controller.State);
        Assert.Equal(0, plcSide.ReadHoldingRegisters(1, TriggerReg, 1)[0]);

        // PLC 之后给出触发 → 才发送 TRIGGER
        plcSide.WriteSingleRegister(1, TriggerReg, 1);
        Assert.True(scanner.WaitForCommand(ScannerCommand.Trigger, TimeSpan.FromSeconds(5)), "PLC 触发后未发送触发命令");
    }

    [Fact]
    public void 退出前强制释放_发送一次RELEASE()
    {
        var config = NewConfig(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));
        using var modbus = ConnectPlc(config);
        using var scanner = new FakeScanner();
        using var controller = new ScannerController(scanner, modbus, config, _dataDir);

        controller.Start();
        controller.ReleaseOnExit();

        Assert.Single(scanner.Commands, c => c.Command == ScannerCommand.Release);
    }

    [Fact]
    public void 退出前释放后_扫码窗口不再重复释放()
    {
        var config = NewConfig(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(800));
        using var modbus = ConnectPlc(config);
        using var scanner = new FakeScanner();
        using var controller = new ScannerController(scanner, modbus, config, _dataDir);
        using var plcSide = CreatePlcSideMaster();

        controller.Start();
        plcSide.WriteSingleRegister(1, TriggerReg, 1);
        Assert.True(scanner.WaitForCommand(ScannerCommand.Trigger, TimeSpan.FromSeconds(5)), "未发出触发命令");

        controller.ReleaseOnExit(); // 软件关闭：先释放
        Thread.Sleep(1200);         // 等过原扫码窗口

        Assert.Single(scanner.Commands, c => c.Command == ScannerCommand.Release);
    }

    /// <summary>假扫码枪通道：记录收到的命令与时刻，可主动推送条码</summary>
    private sealed class FakeScanner : IScannerTransport
    {
        private readonly object _gate = new();
        private readonly List<(string Command, long Ticks)> _commands = new();

        public event Action<string>? BarcodeReceived;
        public event Action<string>? RawDataReceived;
        public event Action<bool>? ConnectionChanged;

        public bool IsOpen { get; private set; } = true;

        public IReadOnlyList<(string Command, long Ticks)> Commands
        {
            get
            {
                lock (_gate)
                {
                    return _commands.ToArray();
                }
            }
        }

        public void EmitBarcode(string barcode)
        {
            RawDataReceived?.Invoke(barcode);
            BarcodeReceived?.Invoke(barcode);
        }

        public bool HasCommand(string command) => Commands.Any(c => c.Command == command);

        public long FirstTicks(string command) => Commands.First(c => c.Command == command).Ticks;

        public long LastTicks(string command) => Commands.Last(c => c.Command == command).Ticks;

        public bool WaitForCommand(string command, TimeSpan timeout)
        {
            var deadline = DateTime.Now + timeout;
            while (DateTime.Now < deadline)
            {
                if (HasCommand(command))
                {
                    return true;
                }

                Thread.Sleep(20);
            }

            return HasCommand(command);
        }

        public void Open(ScannerConfig config)
        {
            IsOpen = true;
            ConnectionChanged?.Invoke(true);
        }

        public void Close()
        {
            IsOpen = false;
            ConnectionChanged?.Invoke(false);
        }

        public bool SendCommand(string ascii)
        {
            if (!IsOpen)
            {
                return false;
            }

            lock (_gate)
            {
                _commands.Add((ascii, Environment.TickCount64));
            }

            return true;
        }

        public void Dispose()
        {
            // 测试替身无需释放
        }
    }
}
