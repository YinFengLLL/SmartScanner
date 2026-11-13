using System.IO.Ports;
using SmartScanner.Core.Controller;
using SmartScanner.Core.Data;
using SmartScanner.Core.Events;
using SmartScanner.Core.Models;
using SmartScanner.Core.Modbus;
using SmartScanner.Core.Scanner;
using SmartScanner.Core.Security;
using SmartScanner.Core.Serial;

namespace SmartScanner.App;

/// <summary>
/// 智能扫码上位机系统主窗体。
/// 布局：顶部参数设置区（仅管理员可编辑）/ 操作控制区 / 扫码信息栏 + 状态日志 / 状态栏（右下切换用户）。
/// 蓝白工业风，启动按钮绿色、停止按钮红色。
/// </summary>
public sealed class MainForm : Form
{
    // ========== 业务对象 ==========
    private readonly ScannerConfig _config;
    private readonly ScannerConfigStore _configStore;
    private IScannerTransport _scanner; // 按上次保存的通讯方式创建（TCP/IP 或串口）
    private readonly ModbusTcpService _modbus = new();
    private readonly ScannerController _controller;
    private readonly AuthService _auth = new();

    // ========== 颜色（蓝白工业风） ==========
    private static readonly Color C_Blue = Color.FromArgb(21, 101, 192);
    private static readonly Color C_Green = Color.FromArgb(0, 176, 80);
    private static readonly Color C_Red = Color.FromArgb(220, 53, 69);
    private static readonly Color C_Bg = Color.FromArgb(240, 246, 252);

    // ========== 控件 ==========
    private GroupBox _grpParams = null!;
    private ComboBox _cmbComPort = null!, _cmbBaudRate = null!, _cmbDataBits = null!, _cmbStopBits = null!;
    private ComboBox _cmbTransportMode = null!;
    private ComboBox _cmbFrameMode = null!;
    private CheckBox _chkTrimAffix = null!;
    private TextBox _txtPlcIp = null!, _txtScannerIp = null!;
    private NumericUpDown _numScannerPort = null!;
    private TextBox _txtWorkerId = null!;
    private NumericUpDown _numPlcPort = null!;
    private NumericUpDown _numInputTrigger = null!, _numWorkerIdReg = null!, _numOutputResult = null!, _numBarcodeReg = null!;
    private ComboBox _cmbBarcodeWriteMode = null!;
    private NumericUpDown _numScanTimeoutSec = null!;
    private Button _btnScanner = null!, _btnConnectPlc = null!, _btnStart = null!, _btnStop = null!;
    private Button _btnManualScan = null!;
    private Button _btnClearInfo = null!, _btnClearLog = null!;
    private TextBox _txtBarcode = null!;
    private RichTextBox _rtxLog = null!;
    private ToolStripStatusLabel _lblStatusUser = null!, _lblStatusSerial = null!, _lblStatusPlc = null!;
    private readonly ToolTip _tips = new();

    public MainForm()
    {
        // 设置记忆：启动时加载上次保存的参数（无配置或损坏则用默认值）
        _configStore = new ScannerConfigStore(Path.Combine(AppContext.BaseDirectory, "config.json"));
        _config = _configStore.Load() ?? new ScannerConfig();

        _scanner = CreateTransport(_config.TransportMode); // 按上次保存的通讯方式建通道
        _controller = new ScannerController(_scanner, _modbus, _config);

        InitializeUi();
        WireEvents();

        // 默认操作员模式（重启恢复操作员）
        UpdateRoleUi();
        UpdateScannerButton(false);
        UpdatePlcButton(false);
        UpdateScanButtons(ScanState.Idle);
        UpdateTransportControls();
        Load += (_, _) =>
        {
            RefreshComPorts();
            AutoConnectAndStart(); // 每次打开软件：自动连接 PLC / 扫码枪并开始扫码监听
        };
        FormClosing += (_, _) =>
        {
            _controller.ReleaseOnExit();          // 退出前释放扫码枪，避免扫码枪灯一直亮
            _configStore.Save(_config);           // 兜底保存
        };
    }

    /// <summary>按通讯方式创建扫码枪通道</summary>
    private static IScannerTransport CreateTransport(ScannerTransportMode mode)
        => mode == ScannerTransportMode.Serial ? new SerialScannerService() : new TcpScannerService();

    // ================= UI 构建 =================

    private void InitializeUi()
    {
        Text = "智能扫码上位机系统";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 720); // 默认窗口 720P（外框 1280×720）
        MinimumSize = new Size(1200, 700);
        BackColor = C_Bg;
        Font = new Font("Microsoft YaHei UI", 9F);

        // ---------- 1. 参数设置区（顶部，仅管理员可编辑） ----------
        _grpParams = new GroupBox
        {
            Text = "参数设置（仅管理员可编辑）",
            Location = new Point(12, 12),
            Size = new Size(1240, 160),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        // 串口与通讯参数行
        AddLabel(_grpParams, "串口号：", 14, 28, width: 64);
        _cmbComPort = AddCombo(_grpParams, 82, 24, 100, ["COM1", "COM2", "COM3", "COM4"]);

        AddLabel(_grpParams, "波特率：", 198, 28, width: 64);
        _cmbBaudRate = AddCombo(_grpParams, 266, 24, 100, ["2400", "4800", "9600", "19200", "38400", "57600", "115200"], "9600");

        AddLabel(_grpParams, "数据位：", 382, 28, width: 64);
        _cmbDataBits = AddCombo(_grpParams, 450, 24, 70, ["8", "7", "6", "5"], "8");

        AddLabel(_grpParams, "停止位：", 536, 28, width: 64);
        _cmbStopBits = AddCombo(_grpParams, 604, 24, 80, ["1", "1.5", "2"], "1");

        AddLabel(_grpParams, "通讯方式：", 700, 28, width: 80);
        _cmbTransportMode = AddCombo(_grpParams, 784, 24, 110, ["TCP/IP", "串口"],
            _config.TransportMode == ScannerTransportMode.Serial ? "串口" : "TCP/IP");

        // 扫码枪数据格式：结束符（后缀）识别方式 + 去前后缀
        AddLabel(_grpParams, "结束符：", 906, 28, width: 62);
        _cmbFrameMode = AddCombo(_grpParams, 970, 24, 142,
            ["自动识别(推荐)", @"CRLF(\r\n)", @"CR(\r)", @"LF(\n)", "无前后缀"],
            FrameModeText(_config.FrameMode));
        _chkTrimAffix = new CheckBox
        {
            Text = "去前后缀",
            Checked = _config.TrimFrameAffixes,
            Location = new Point(1118, 26),
            Size = new Size(108, 22),
            AutoSize = false,
        };
        _grpParams.Controls.Add(_chkTrimAffix);

        // PLC 参数行
        AddLabel(_grpParams, "PLC 参数", 14, 64, bold: true);
        AddLabel(_grpParams, "IP 地址：", 100, 64, width: 64);
        _txtPlcIp = new TextBox
        {
            Text = _config.PlcIp,
            Location = new Point(168, 60),
            Size = new Size(150, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        _grpParams.Controls.Add(_txtPlcIp);

        AddLabel(_grpParams, "端口号：", 334, 64, width: 58);
        _numPlcPort = AddNumeric(_grpParams, 396, 60, 70, 1, 65535, _config.PlcPort);

        AddLabel(_grpParams, "扫码枪 IP：", 482, 64, width: 80);
        _txtScannerIp = new TextBox
        {
            Text = _config.ScannerIp,
            Location = new Point(566, 60),
            Size = new Size(140, 26),
        };
        _grpParams.Controls.Add(_txtScannerIp);
        AddLabel(_grpParams, "端口号：", 722, 64, width: 58);
        _numScannerPort = AddNumeric(_grpParams, 784, 60, 70, 1, 65535, _config.ScannerPort);

        AddLabel(_grpParams, "扫码超时（秒）：", 870, 64, width: 104);
        _numScanTimeoutSec = AddNumeric(_grpParams, 978, 60, 70, 1, 600, (decimal)_config.ScanTimeout.TotalSeconds);

        // 寄存器映射行（标签固定宽度，避免与输入框文字重叠；间距充足）
        AddLabel(_grpParams, "寄存器映射", 14, 100, bold: true);
        AddLabel(_grpParams, "输入触发寄存器：", 96, 100, width: 112);
        _numInputTrigger = AddNumeric(_grpParams, 212, 96, 70, 0, 65535, _config.InputTriggerRegister);
        AddLabel(_grpParams, "工号寄存器：", 298, 100, width: 96);
        _numWorkerIdReg = AddNumeric(_grpParams, 398, 96, 70, 0, 65535, _config.WorkerIdRegister);
        AddLabel(_grpParams, "输出结果寄存器：", 484, 100, width: 124);
        _numOutputResult = AddNumeric(_grpParams, 612, 96, 70, 0, 65535, _config.OutputResultRegister);
        AddLabel(_grpParams, "条码内容寄存器：", 698, 100, width: 124);
        _numBarcodeReg = AddNumeric(_grpParams, 826, 96, 70, 0, 65535, _config.BarcodeRegister);
        AddLabel(_grpParams, "条码写入方式：", 912, 100, width: 112);
        _cmbBarcodeWriteMode = AddCombo(_grpParams, 1028, 96, 124,
            ["INT(16位)", "DINT(32位)", "STRING"], BarcodeWriteModeText(_config.BarcodeWriteMode));

        // 流程说明（完整可读，替代原先被压缩的字段名）
        var hint = AddLabel(_grpParams,
            "流程说明：PLC 置 1 → 上位机清零并触发扫码（保持时间 = 扫码超时）→ 扫码成功先写条码内容、再写结果 1 并立即释放；超时写 2 后释放。扫码枪返回数据带不带前缀后缀均可识别。",
            14, 130, width: 1208);
        hint.ForeColor = Color.FromArgb(90, 90, 90);

        Controls.Add(_grpParams);

        // ---------- 2. 操作控制区 ----------
        var grpOps = new GroupBox
        {
            Text = "操作控制",
            Location = new Point(12, 182),
            Size = new Size(1240, 66),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _btnScanner = AddButton(grpOps, "连接扫码枪", 20, 24, 120, C_Blue);
        _btnConnectPlc = AddButton(grpOps, "连接 PLC", 156, 24, 110, C_Blue);
        _btnStart = AddButton(grpOps, "开始扫码", 282, 24, 130, C_Green);
        _btnStop = AddButton(grpOps, "停止扫码", 428, 24, 130, C_Red);
        AddLabel(grpOps, "工号：", 590, 28, width: 50);
        _txtWorkerId = new TextBox
        {
            Text = _config.DefaultWorkerId,
            Location = new Point(644, 24),
            Size = new Size(110, 26),
        };
        grpOps.Controls.Add(_txtWorkerId);
        _btnManualScan = AddButton(grpOps, "手动扫码", 790, 24, 120, C_Blue);
        Controls.Add(grpOps);

        // ---------- 3. 扫码信息栏 ----------
        var grpInfo = new GroupBox
        {
            Text = "扫码信息（最新在前，最多显示 200 条）",
            Location = new Point(12, 258),
            Size = new Size(720, 380),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
        };
        _txtBarcode = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Location = new Point(14, 24),
            Size = new Size(692, 300),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        grpInfo.Controls.Add(_txtBarcode);
        _btnClearInfo = AddButton(grpInfo, "清除信息", 616, 338, 90, C_Blue, AnchorStyles.Bottom | AnchorStyles.Right);
        Controls.Add(grpInfo);

        // ---------- 4. 状态日志 ----------
        var grpLog = new GroupBox
        {
            Text = "状态日志（含条码与寄存器写入结果）",
            Location = new Point(744, 258),
            Size = new Size(508, 380),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        _rtxLog = new RichTextBox
        {
            ReadOnly = true,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Location = new Point(14, 24),
            Size = new Size(480, 300),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        grpLog.Controls.Add(_rtxLog);
        _btnClearLog = AddButton(grpLog, "清除日志", 404, 338, 90, C_Blue, AnchorStyles.Bottom | AnchorStyles.Right);
        Controls.Add(grpLog);

        // ---------- 5. 状态栏（右下角切换用户） ----------
        var status = new StatusStrip { SizingGrip = false };
        _lblStatusUser = new ToolStripStatusLabel { Text = "当前用户：操作员" };
        _lblStatusSerial = new ToolStripStatusLabel { Text = "扫码枪：未连接" };
        _lblStatusPlc = new ToolStripStatusLabel { Text = "PLC：未连接" };
        var spring = new ToolStripStatusLabel { Spring = true };
        var btnSwitchUser = new ToolStripButton
        {
            Text = "切换用户",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            BackColor = Color.FromArgb(225, 235, 245),
        };
        btnSwitchUser.Click += (_, _) => SwitchUser();
        status.Items.AddRange(new ToolStripItem[] { _lblStatusUser, _lblStatusSerial, _lblStatusPlc, spring, btnSwitchUser });
        Controls.Add(status);

        // ---------- 6. 控件提示（完整说明，弥补界面空间限制） ----------
        _tips.SetToolTip(_numInputTrigger, "PLC 将本寄存器置 1 触发一次扫码；上位机读到后写 0 应答。");
        _tips.SetToolTip(_numWorkerIdReg, "PLC 写入当前工号数值；读 0 时回退使用界面工号。");
        _tips.SetToolTip(_numOutputResult, "扫码成功写 1（OK）；超时未扫码写 2（NG）。");
        _tips.SetToolTip(_numBarcodeReg, "扫码成功后先写条码内容，再写输出结果 = 1；条码同步保存到 Data\\YYYYMMDD.txt。");
        _tips.SetToolTip(_cmbBarcodeWriteMode, "INT(16位)：1 个寄存器，0~65535；DINT(32位)：2 个寄存器（高字在前）；STRING：多寄存器 ASCII。");
        _tips.SetToolTip(_numScanTimeoutSec, "触发后扫码枪保持扫码的时间（触发→释放间隔）；超时写结果 2（NG）后释放，扫码成功则提前释放。");
        _tips.SetToolTip(_cmbFrameMode,
            "扫码枪返回数据的结束符（后缀）。自动识别：CR / LF / CRLF 都能切帧，且扫码枪没有前后缀时按静默 150ms 分帧；" +
            "扫码枪返回信息不带前缀和后缀时保持“自动识别”即可，无需给扫码枪加后缀。修改后需重新连接扫码枪生效。");
        _tips.SetToolTip(_chkTrimAffix, "去掉条码前后的不可见字符（STX/ETX/空格等前后缀），一般保持勾选。修改后需重新连接扫码枪生效。");
        _tips.SetToolTip(_txtWorkerId, "PLC 工号寄存器为 0 时使用此工号。");
    }

    private void WireEvents()
    {
        _controller.Logged += e => Ui(() => AppendLog(e));
        _controller.BarcodeScanned += b => Ui(() => PrependBarcode(b));
        _controller.StateChanged += s => Ui(() => UpdateScanButtons(s));
        _scanner.ConnectionChanged += open => Ui(() => UpdateScannerButton(open));
        _modbus.ConnectionChanged += c => Ui(() => UpdatePlcButton(c));
        _auth.RoleChanged += _ => Ui(UpdateRoleUi);

        _btnScanner.Click += (_, _) => ToggleScanner();
        _btnConnectPlc.Click += (_, _) => TogglePlc();
        _btnStart.Click += (_, _) => RunAsync("开始扫码", _controller.Start);
        _btnStop.Click += (_, _) => RunAsync("停止扫码", _controller.Stop);
        _btnManualScan.Click += (_, _) => RunAsync("手动扫码", _controller.ManualScan);
        _btnClearInfo.Click += (_, _) =>
        {
            _txtBarcode.Clear();
            _controller.ResetRecordNumber();
            AppendLog(new LogEntry(DateTime.Now, LogLevel.Info, "已清除扫码信息，记录编号重新从 001 开始"));
        };
        _btnClearLog.Click += (_, _) => _rtxLog.Clear();

        _cmbComPort.DropDown += (_, _) => RefreshComPorts();
        _cmbTransportMode.SelectedIndexChanged += (_, _) => OnTransportModeChanged();
    }

    // ================= 开机自动连接与开始监听 =================

    /// <summary>
    /// 每次打开软件自动执行：连接 PLC → 连接（打开）扫码枪 → 开始扫码监听。
    /// 三步在后台线程依次执行，任意一步失败只记录日志，不影响其余步骤，也不影响手动按钮。
    /// </summary>
    private void AutoConnectAndStart()
    {
        ApplyUiToConfig(); // 用界面参数（= 上次保存的配置）连接

        AppendLog(new LogEntry(DateTime.Now, LogLevel.Info,
            "软件启动：正在自动连接 PLC、扫码枪并开始扫码监听..."));

        Task.Run(() =>
        {
            bool plcOk = false;
            bool scannerOk = false;

            try
            {
                if (!_modbus.IsConnected)
                {
                    _controller.ConnectPlc();
                }

                plcOk = true;
            }
            catch (Exception ex)
            {
                Ui(() => AppendLog(new LogEntry(DateTime.Now, LogLevel.Error, $"自动连接 PLC 失败: {ex.Message}")));
            }

            try
            {
                if (!_scanner.IsOpen)
                {
                    _controller.OpenScanner();
                }

                scannerOk = true;
            }
            catch (Exception ex)
            {
                Ui(() => AppendLog(new LogEntry(DateTime.Now, LogLevel.Error, $"自动连接扫码枪失败: {ex.Message}")));
            }

            if (!plcOk || !scannerOk)
            {
                Ui(() => AppendLog(new LogEntry(DateTime.Now, LogLevel.Warn,
                    "自动启动未完成，请手动点击“连接 PLC / 连接扫码枪 / 开始扫码”重试")));
                return;
            }

            try
            {
                if (_controller.State == ScanState.Idle)
                {
                    _controller.Start();
                }

                Ui(() => AppendLog(new LogEntry(DateTime.Now, LogLevel.Success,
                    "自动启动完成：PLC 与扫码枪已连接，已开始扫码监听")));
            }
            catch (Exception ex)
            {
                Ui(() => AppendLog(new LogEntry(DateTime.Now, LogLevel.Error, $"自动开始扫码失败: {ex.Message}")));
            }
        });
    }

    // ================= 事件处理 =================

    private void ToggleScanner()
    {
        if (_scanner.IsOpen)
        {
            RunAsync("断开扫码枪", _controller.CloseScanner);
            return;
        }

        ApplyUiToConfig();
        string op = IsTcpMode ? "连接扫码枪" : "打开串口";
        RunAsync(op, _controller.OpenScanner);
    }

    private void TogglePlc()
    {
        if (_modbus.IsConnected)
        {
            RunAsync("断开PLC", _controller.DisconnectPlc);
            return;
        }

        ApplyUiToConfig();
        RunAsync("连接PLC", _controller.ConnectPlc);
    }

    private void SwitchUser()
    {
        if (_auth.IsAdmin)
        {
            _auth.Logout();
            AppendLog(new LogEntry(DateTime.Now, LogLevel.Info, "已切换至操作员模式"));
            return;
        }

        using var dlg = new LoginDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (_auth.TryLoginAsAdmin(dlg.Password))
        {
            AppendLog(new LogEntry(DateTime.Now, LogLevel.Success, "已切换至管理员模式"));
        }
        else
        {
            MessageBox.Show(this, "密码错误！", "登录失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RefreshComPorts()
    {
        string? selected = _cmbComPort.SelectedItem?.ToString();
        string[] ports = SerialScannerService.GetAvailablePorts();

        _cmbComPort.Items.Clear();
        if (ports.Length == 0)
        {
            _cmbComPort.Items.Add("（无可用串口）");
            _cmbComPort.Enabled = false;
        }
        else
        {
            _cmbComPort.Items.AddRange(ports);
            _cmbComPort.Enabled = true;
            _cmbComPort.SelectedItem = selected ?? ports.FirstOrDefault(p => p == _config.ComPort) ?? ports[0];
        }
    }

    private void ApplyUiToConfig()
    {
        _config.ComPort = _cmbComPort.SelectedItem?.ToString() ?? _config.ComPort;
        _config.BaudRate = int.TryParse(_cmbBaudRate.SelectedItem?.ToString(), out int baud) ? baud : _config.BaudRate;
        _config.DataBits = int.TryParse(_cmbDataBits.SelectedItem?.ToString(), out int bits) ? bits : _config.DataBits;
        _config.StopBits = _cmbStopBits.SelectedIndex switch
        {
            1 => StopBits.OnePointFive,
            2 => StopBits.Two,
            _ => StopBits.One,
        };
        _config.PlcIp = _txtPlcIp.Text.Trim();
        string workerId = _txtWorkerId.Text.Trim();
        if (!string.IsNullOrWhiteSpace(workerId))
        {
            _config.DefaultWorkerId = workerId;
        }
        _config.PlcPort = (int)_numPlcPort.Value;
        _config.TransportMode = IsTcpMode ? ScannerTransportMode.Tcp : ScannerTransportMode.Serial;
        _config.ScannerIp = _txtScannerIp.Text.Trim();
        _config.ScannerPort = (int)_numScannerPort.Value;
        _config.FrameMode = _cmbFrameMode.SelectedIndex switch
        {
            1 => ScannerFrameMode.CrLf,
            2 => ScannerFrameMode.Cr,
            3 => ScannerFrameMode.Lf,
            4 => ScannerFrameMode.None,
            _ => ScannerFrameMode.Auto,
        };
        _config.TrimFrameAffixes = _chkTrimAffix.Checked;
        _config.InputTriggerRegister = (ushort)_numInputTrigger.Value;
        _config.WorkerIdRegister = (ushort)_numWorkerIdReg.Value;
        _config.OutputResultRegister = (ushort)_numOutputResult.Value;
        _config.BarcodeRegister = (ushort)_numBarcodeReg.Value;
        _config.BarcodeWriteMode = _cmbBarcodeWriteMode.SelectedIndex switch
        {
            1 => BarcodeWriteMode.Int32,
            2 => BarcodeWriteMode.String,
            _ => BarcodeWriteMode.Int16,
        };
        _config.ScanTimeout = TimeSpan.FromSeconds((double)_numScanTimeoutSec.Value);

        // 设置记忆：修改即存（防崩溃/断电丢配置）
        _configStore.Save(_config);
    }

    // ================= UI 状态刷新 =================

    private void UpdateRoleUi()
    {
        // 参数设置区整体置灰（操作员模式不可编辑）；手动扫码仅管理员可用
        _grpParams.Enabled = _auth.IsAdmin;
        _btnManualScan.Enabled = _auth.IsAdmin;
        _lblStatusUser.Text = "当前用户：" + _auth.RoleDisplayName;
    }

    private void UpdateScannerButton(bool open)
    {
        bool tcp = IsTcpMode;
        _btnScanner.Text = open
            ? (tcp ? "断开扫码枪" : "关闭串口")
            : (tcp ? "连接扫码枪" : "打开串口");
        _btnScanner.BackColor = open ? C_Green : C_Blue;
        _lblStatusSerial.Text = open
            ? (tcp ? "扫码枪：已连接" : "串口：已打开")
            : (tcp ? "扫码枪：未连接" : "串口：未连接");
    }

    /// <summary>当前是否为 TCP/IP 通讯方式（下拉第一项）</summary>
    private bool IsTcpMode => _cmbTransportMode.SelectedIndex == 0;

    /// <summary>条码写入方式的界面显示文本</summary>
    private static string BarcodeWriteModeText(BarcodeWriteMode mode) => mode switch
    {
        BarcodeWriteMode.Int32 => "DINT(32位)",
        BarcodeWriteMode.String => "STRING",
        _ => "INT(16位)",
    };

    /// <summary>结束符（前后缀识别方式）的界面显示文本</summary>
    private static string FrameModeText(ScannerFrameMode mode) => mode switch
    {
        ScannerFrameMode.CrLf => @"CRLF(\r\n)",
        ScannerFrameMode.Cr => @"CR(\r)",
        ScannerFrameMode.Lf => @"LF(\n)",
        ScannerFrameMode.None => "无前后缀",
        _ => "自动识别(推荐)",
    };

    /// <summary>通讯方式切换：停止扫码、重建扫码枪通讯通道（断开旧连接）</summary>
    private void OnTransportModeChanged()
    {
        bool tcp = IsTcpMode;
        if (_controller.State != ScanState.Idle)
        {
            _controller.Stop();
        }

        IScannerTransport next = CreateTransport(tcp ? ScannerTransportMode.Tcp : ScannerTransportMode.Serial);
        if (_scanner.IsOpen)
        {
            _scanner.Close();
            AppendLog(new LogEntry(DateTime.Now, LogLevel.Info,
                tcp ? "已断开串口，切换为 TCP/IP 通讯" : "已断开 TCP 连接，切换为串口通讯"));
        }

        _controller.SetScanner(next);
        _scanner = next;
        UpdateTransportControls();
        UpdateScannerButton(false);
    }

    /// <summary>按通讯方式启用/禁用对应参数控件</summary>
    private void UpdateTransportControls()
    {
        bool tcp = IsTcpMode;
        _cmbComPort.Enabled = !tcp;
        _cmbBaudRate.Enabled = !tcp;
        _cmbDataBits.Enabled = !tcp;
        _cmbStopBits.Enabled = !tcp;
        _txtScannerIp.Enabled = tcp;
        _numScannerPort.Enabled = tcp;
    }

    private void UpdatePlcButton(bool connected)
    {
        _btnConnectPlc.Text = connected ? "断开PLC" : "连接PLC";
        _btnConnectPlc.BackColor = connected ? C_Green : C_Blue;
        _lblStatusPlc.Text = connected ? "PLC：已连接" : "PLC：未连接";
    }

    private void UpdateScanButtons(ScanState state)
    {
        bool busy = state != ScanState.Idle;
        _btnStart.Enabled = !busy;
        _btnStop.Enabled = busy;
        _btnStart.Text = state switch
        {
            ScanState.Listening => "监听中...",
            ScanState.WaitingBarcode => "等待扫码...",
            _ => "开始扫码",
        };
    }

    private void PrependBarcode(string barcode)
    {
        // 最新一条置顶
        _txtBarcode.Text = string.IsNullOrEmpty(_txtBarcode.Text)
            ? barcode
            : barcode + Environment.NewLine + _txtBarcode.Text;

        var lines = _txtBarcode.Lines;
        if (lines.Length > 200)
        {
            _txtBarcode.Lines = lines.Take(200).ToArray();
        }
    }

    private void AppendLog(LogEntry entry)
    {
        _rtxLog.SelectionStart = _rtxLog.TextLength;
        _rtxLog.SelectionLength = 0;
        _rtxLog.SelectionColor = entry.Level switch
        {
            LogLevel.Error => Color.Red,
            LogLevel.Success => Color.FromArgb(0, 128, 0),
            LogLevel.Warn => Color.OrangeRed,
            _ => Color.Black,
        };
        _rtxLog.AppendText(entry.ToString() + Environment.NewLine);
        _rtxLog.ScrollToCaret();
    }

    // ================= 辅助 =================

    /// <summary>后台执行操作，异常统一记录到日志（不阻塞 UI）</summary>
    private void RunAsync(string op, Action action)
    {
        Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Ui(() => AppendLog(new LogEntry(DateTime.Now, LogLevel.Error, $"{op}失败: {ex.Message}")));
            }
        });
    }

    /// <summary>线程安全地更新 UI：跨线程时通过消息队列调度</summary>
    private void Ui(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
        }
        else
        {
            action();
        }
    }

    private static Label AddLabel(Control parent, string text, int x, int y, bool bold = false, int width = 0)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = width <= 0,
            Size = width > 0 ? new Size(width, 20) : Size.Empty,
            Location = new Point(x, y + 3),
            TextAlign = width > 0 ? ContentAlignment.MiddleLeft : ContentAlignment.TopLeft,
            Font = bold ? new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) : null,
        };
        parent.Controls.Add(lbl);
        return lbl;
    }

    private static ComboBox AddCombo(Control parent, int x, int y, int width, string[] items, string? selected = null)
    {
        var cmb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(x, y),
            Size = new Size(width, 26),
        };
        cmb.Items.AddRange(items);
        if (selected is not null)
        {
            cmb.SelectedItem = selected;
        }

        parent.Controls.Add(cmb);
        return cmb;
    }

    private static NumericUpDown AddNumeric(Control parent, int x, int y, int width, decimal min, decimal max, decimal value)
    {
        var num = new NumericUpDown
        {
            Location = new Point(x, y),
            Size = new Size(width, 26),
            Minimum = min,
            Maximum = max,
            Value = value,
        };
        parent.Controls.Add(num);
        return num;
    }

    private static Button AddButton(Control parent, string text, int x, int y, int width, Color backColor, AnchorStyles? anchor = null)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 32),
            BackColor = backColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Anchor = anchor ?? AnchorStyles.Top | AnchorStyles.Left,
        };
        parent.Controls.Add(btn);
        return btn;
    }
}
