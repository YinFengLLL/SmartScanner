using System.Net.Sockets;
using NModbus;
using SmartScanner.Core.Models;

namespace SmartScanner.Core.Modbus;

/// <summary>
/// Modbus TCP 主站封装（基于 NModbus）：
/// 连接/断开、读保持寄存器(FC3)、写单个寄存器(FC6)。
/// 非线程安全，所有读写以 lock 串行化；异常上抛由调用方记录日志。
/// </summary>
public sealed class ModbusTcpService : IDisposable
{
    private readonly object _sync = new();
    private TcpClient? _tcp;
    private IModbusMaster? _master;

    /// <summary>连接状态变化</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected => _master is not null;

    /// <summary>连接到 PLC；连接/握手超时抛异常</summary>
    public void Connect(ScannerConfig config, TimeSpan connectTimeout)
    {
        if (IsConnected)
        {
            return;
        }

        var tcp = new TcpClient();
        Task connectTask = tcp.ConnectAsync(config.PlcIp, config.PlcPort);
        if (!connectTask.Wait(connectTimeout))
        {
            tcp.Dispose();
            throw new TimeoutException($"连接 PLC {config.PlcIp}:{config.PlcPort} 超时");
        }

        // NModbus 工厂创建主站
        var factory = new ModbusFactory();
        IModbusMaster master = factory.CreateMaster(tcp);
        master.Transport.ReadTimeout = (int)config.ModbusTimeout.TotalMilliseconds;
        master.Transport.WriteTimeout = (int)config.ModbusTimeout.TotalMilliseconds;

        _tcp = tcp;
        _master = master;
        ConnectionChanged?.Invoke(true);
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            _master?.Dispose();
            _master = null;
            _tcp?.Dispose();
            _tcp = null;
        }

        ConnectionChanged?.Invoke(false);
    }

    /// <summary>读保持寄存器（FC3）</summary>
    public ushort[] ReadHoldingRegisters(byte slaveId, ushort startAddress, ushort count)
    {
        lock (_sync)
        {
            EnsureConnected();
            return _master!.ReadHoldingRegisters(slaveId, startAddress, count);
        }
    }

    /// <summary>写单个保持寄存器（FC6）</summary>
    public void WriteSingleRegister(byte slaveId, ushort address, ushort value)
    {
        lock (_sync)
        {
            EnsureConnected();
            _master!.WriteSingleRegister(slaveId, address, value);
        }
    }

    private void EnsureConnected()
    {
        if (_master is null)
        {
            throw new InvalidOperationException("PLC 未连接，请先连接 PLC");
        }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
