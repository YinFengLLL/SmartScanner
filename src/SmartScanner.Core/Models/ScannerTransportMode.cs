namespace SmartScanner.Core.Models;

/// <summary>扫码枪通讯方式</summary>
public enum ScannerTransportMode
{
    /// <summary>串口（USB COM）</summary>
    Serial,

    /// <summary>TCP/IP CLIENT（上位机作为客户端连接扫码枪）</summary>
    Tcp,
}
