# SmartScanner

智能扫码上位机系统（C# WinForms，.NET 10）

> 本仓库为 2026-08-10 源码备份快照（MES 上传功能扩展前版本）。

## 功能
- 扫码枪通讯：串口（USB COM）与 TCP/IP CLIENT 双模式
- PLC 交互：Modbus TCP，4 个保持寄存器（输入触发 4000 / 工号 4001 / 输出结果 4100 / 输出备用 4101）
- 条码仅显示于界面，生成本地 TXT 存档（Data\YYYYMMDD.txt，CSV 格式）
- 操作员/管理员权限切换（默认密码 admin123，仅管理员可改参数）
- 配置记忆（config.json 自动保存/加载）

## 结构
- `src/SmartScanner.Core` — 通信与业务层（串口 / Modbus TCP / PLC 触发监听 / 权限 / 日志）
- `src/SmartScanner.App` — WinForms 主界面
- `tests/SmartScanner.Core.Tests` — 单元与集成测试

## 构建
```
dotnet build SmartScanner.slnx
dotnet test SmartScanner.slnx
```
