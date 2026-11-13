using System.Net;
using System.Net.Sockets;
using NModbus;
using SmartScanner.Core.Handshake;
using SmartScanner.Core.Models;
using SmartScanner.Core.Modbus;

namespace SmartScanner.Core.Tests;

/// <summary>
/// PlcTriggerService 与本地 Modbus TCP 从站（模拟 PLC）的集成测试。
/// 每个测试启动独立从站与随机端口，验证"触发→清零→写结果(1/2)"完整链路。
/// </summary>
public sealed class PlcTriggerServiceTests : IDisposable
{
    private const ushort TriggerReg = 4000; // 与 ScannerConfig 默认一致
    private const ushort ResultReg = 4100;
    private const ushort BarcodeReg = 4101;

    private readonly ModbusFactory _factory = new();
    private readonly IModbusTcpSlaveNetwork _network;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _port;

    public PlcTriggerServiceTests()
    {
        _port = GetFreePort();
        _network = _factory.CreateSlaveNetwork(new TcpListener(IPAddress.Loopback, _port));
        _network.AddSlave(_factory.CreateSlave(1));
        _ = Task.Run(() => _network.ListenAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _network.Dispose();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static ScannerConfig NewConfig(int port, TimeSpan poll, TimeSpan timeout) => new()
    {
        PlcIp = "127.0.0.1",
        PlcPort = port,
        SlaveId = 1,
        PollInterval = poll,
        ScanTimeout = timeout,
        ModbusTimeout = TimeSpan.FromMilliseconds(1000),
    };

    private static ModbusTcpService ConnectPlc(ScannerConfig config)
    {
        var modbus = new ModbusTcpService();
        modbus.Connect(config, TimeSpan.FromSeconds(3));
        return modbus;
    }

    /// <summary>独立 master 模拟 PLC 侧读写</summary>
    private IModbusMaster CreatePlcSideMaster()
    {
        var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, _port);
        return _factory.CreateMaster(tcp);
    }

    [Fact]
    public void 触发后清零并等待_扫码成功写结果1()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        var triggered = new ManualResetEventSlim();
        ushort? written = null;
        var writtenEvt = new ManualResetEventSlim();
        trigger.TriggerReceived += () => triggered.Set();
        trigger.ResultWritten += r =>
        {
            written = r;
            writtenEvt.Set();
        };

        trigger.Start();

        // PLC 侧触发：写 4000 = 1
        plcSide.WriteSingleRegister(1, TriggerReg, 1);

        // 上位机应清零 4000 并进入等待扫码状态
        Assert.True(triggered.Wait(TimeSpan.FromSeconds(5)), "未收到触发事件");
        Assert.True(trigger.IsWaiting);
        Assert.Equal(0, plcSide.ReadHoldingRegisters(1, TriggerReg, 1)[0]);

        // 扫码：模拟条码到达
        trigger.HandleBarcode();

        Assert.True(writtenEvt.Wait(TimeSpan.FromSeconds(5)), "未收到结果写入事件");
        Assert.Equal(PlcTriggerService.ResultOk, written);
        Assert.Equal(1, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]);
        Assert.False(trigger.IsWaiting);

        trigger.Stop();
    }

    [Fact]
    public void 扫码成功_条码内容按INT16写入单寄存器()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
        config.BarcodeRegister = BarcodeReg;
        config.BarcodeWriteMode = BarcodeWriteMode.Int16;
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        var triggered = new ManualResetEventSlim();
        trigger.TriggerReceived += () => triggered.Set();

        trigger.Start();
        plcSide.WriteSingleRegister(1, TriggerReg, 1);
        Assert.True(triggered.Wait(TimeSpan.FromSeconds(5)), "未收到触发事件");

        // 11 位补零条码 → 数值 100，写入 1 个寄存器
        BarcodeWriteOutcome? outcome = trigger.HandleBarcode("00000000100");

        Assert.True(outcome?.Success);
        Assert.Equal(100, plcSide.ReadHoldingRegisters(1, BarcodeReg, 1)[0]);
        Assert.Equal(1, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]); // 条码先写、OK 后写

        trigger.Stop();
    }

    [Fact]
    public void 扫码成功_条码内容按INT32写入双寄存器且高字在前()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
        config.BarcodeRegister = BarcodeReg;
        config.BarcodeWriteMode = BarcodeWriteMode.Int32;
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        var triggered = new ManualResetEventSlim();
        trigger.TriggerReceived += () => triggered.Set();

        trigger.Start();
        plcSide.WriteSingleRegister(1, TriggerReg, 1);
        Assert.True(triggered.Wait(TimeSpan.FromSeconds(5)), "未收到触发事件");

        BarcodeWriteOutcome? outcome = trigger.HandleBarcode("0000123456"); // 123456 > 65535

        Assert.True(outcome?.Success);
        ushort[] registers = plcSide.ReadHoldingRegisters(1, BarcodeReg, 2);
        Assert.Equal((ushort)(123456 >> 16), registers[0]);
        Assert.Equal((ushort)(123456 & 0xFFFF), registers[1]);

        trigger.Stop();
    }

    [Fact]
    public void 触发后超时未扫码_写结果2()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300));
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        var writtenEvt = new ManualResetEventSlim();
        ushort? written = null;
        trigger.ResultWritten += r =>
        {
            written = r;
            writtenEvt.Set();
        };

        trigger.Start();
        plcSide.WriteSingleRegister(1, TriggerReg, 1);

        Assert.True(writtenEvt.Wait(TimeSpan.FromSeconds(5)), "超时结果未写入");
        Assert.Equal(PlcTriggerService.ResultTimeout, written);
        Assert.Equal(2, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]);
        Assert.False(trigger.IsWaiting);

        trigger.Stop();
    }

    [Fact]
    public void 启动前触发寄存器已是1_只清零不触发()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300));
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        int triggerCount = 0;
        var triggered = new ManualResetEventSlim();
        trigger.TriggerReceived += () =>
        {
            Interlocked.Increment(ref triggerCount);
            triggered.Set();
        };

        // 上一轮遗留（或开机前已置位）的触发信号
        plcSide.WriteSingleRegister(1, TriggerReg, 1);

        Assert.True(trigger.ClearStaleTrigger(), "未识别出残留触发信号");
        trigger.Start();

        // 开始监听后不得直接扫码，只清零应答
        Thread.Sleep(400);
        Assert.False(triggered.IsSet, "开始监听时直接触发了一次扫码");
        Assert.Equal(0, plcSide.ReadHoldingRegisters(1, TriggerReg, 1)[0]);
        Assert.Equal(0, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]); // 没有进入等待/超时写结果

        // PLC 重新给出触发 → 正常扫码流程
        plcSide.WriteSingleRegister(1, TriggerReg, 1);
        Assert.True(triggered.Wait(TimeSpan.FromSeconds(5)), "新触发未被响应");
        Assert.Equal(1, triggerCount);
        trigger.Stop();
    }

    [Fact]
    public void 未触发时结果寄存器保持不变()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        trigger.Start();
        Thread.Sleep(300); // 轮询若干轮

        Assert.Equal(0, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]);
        Assert.False(trigger.IsWaiting);

        trigger.Stop();
    }

    [Fact]
    public void 停止后不再响应新触发()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        var triggered = new ManualResetEventSlim();
        trigger.TriggerReceived += () => triggered.Set();

        trigger.Start();
        trigger.Stop();
        plcSide.WriteSingleRegister(1, TriggerReg, 1);

        // 停止后触发寄存器不会被清零（不响应）
        Thread.Sleep(300);
        Assert.Equal(1, plcSide.ReadHoldingRegisters(1, TriggerReg, 1)[0]);
        Assert.False(trigger.IsWaiting);
    }

    [Fact]
    public void 手动触发等价于PLC触发_扫码成功写结果1()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        var manualEvt = new ManualResetEventSlim();
        ushort? written = null;
        var writtenEvt = new ManualResetEventSlim();
        trigger.ManualTriggered += () => manualEvt.Set();
        trigger.ResultWritten += r =>
        {
            written = r;
            writtenEvt.Set();
        };

        trigger.Start();
        trigger.ManualTrigger();

        Assert.True(manualEvt.Wait(TimeSpan.FromSeconds(5)), "未收到手动触发事件");
        Assert.True(trigger.IsWaiting);

        // 扫码：模拟条码到达
        trigger.HandleBarcode();

        Assert.True(writtenEvt.Wait(TimeSpan.FromSeconds(5)), "未收到结果写入事件");
        Assert.Equal(PlcTriggerService.ResultOk, written);
        Assert.Equal(1, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]);

        trigger.Stop();
    }

    [Fact]
    public void 结果写入后PLC保持触发_不再重复写入结果()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        int writeCount = 0;
        var firstWrite = new ManualResetEventSlim();
        trigger.ResultWritten += r =>
        {
            Interlocked.Increment(ref writeCount);
            firstWrite.Set();
        };

        trigger.Start();
        plcSide.WriteSingleRegister(1, TriggerReg, 1); // PLC 触发

        Assert.True(firstWrite.Wait(TimeSpan.FromSeconds(5)), "首次结果未写入");
        Assert.Equal(1, writeCount);

        // PLC 保持 4000=1（未复位）：继续轮询多个周期，4100 不应被重复写入
        Thread.Sleep(500);
        Assert.Equal(1, writeCount);
        Assert.Equal(2, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]); // 仍是首次写入的 2

        trigger.Stop();
    }

    [Fact]
    public void PLC复位后可再次触发_第二轮扫码写结果1()
    {
        var config = NewConfig(_port, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300));
        using var modbus = ConnectPlc(config);
        using var trigger = new PlcTriggerService(modbus, config);
        using var plcSide = CreatePlcSideMaster();

        int triggerCount = 0;
        int writeCount = 0;
        var secondTrigger = new ManualResetEventSlim();
        var secondWrite = new ManualResetEventSlim();
        trigger.TriggerReceived += () =>
        {
            if (Interlocked.Increment(ref triggerCount) == 2)
            {
                secondTrigger.Set();
            }
        };
        trigger.ResultWritten += _ =>
        {
            if (Interlocked.Increment(ref writeCount) == 2)
            {
                secondWrite.Set();
            }
        };

        trigger.Start();

        // 第一轮：触发 → 超时写 2
        plcSide.WriteSingleRegister(1, TriggerReg, 1);
        Thread.Sleep(600);

        // PLC 复位 4000=0 → 抑制解除
        plcSide.WriteSingleRegister(1, TriggerReg, 0);
        Thread.Sleep(200);

        // 第二轮触发 → 等待
        plcSide.WriteSingleRegister(1, TriggerReg, 1);
        Assert.True(secondTrigger.Wait(TimeSpan.FromSeconds(5)), "第二轮触发未到达");

        // 第二轮扫码成功 → 写 1
        trigger.HandleBarcode();
        Assert.True(secondWrite.Wait(TimeSpan.FromSeconds(5)), "第二轮结果未写入");

        Assert.Equal(2, writeCount);
        Assert.Equal(1, plcSide.ReadHoldingRegisters(1, ResultReg, 1)[0]);

        trigger.Stop();
    }
}
