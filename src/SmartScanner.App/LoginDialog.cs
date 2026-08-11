namespace SmartScanner.App;

/// <summary>
/// 管理员登录对话框：密码输入框（掩码显示），确定/取消。
/// </summary>
public sealed class LoginDialog : Form
{
    private readonly TextBox _txtPassword;

    public LoginDialog()
    {
        Text = "管理员登录";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(320, 140);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        var lbl = new Label
        {
            Text = "请输入管理员密码：",
            AutoSize = true,
            Location = new Point(24, 22),
        };

        _txtPassword = new TextBox
        {
            Location = new Point(24, 50),
            Size = new Size(272, 26),
            UseSystemPasswordChar = true,
            MaxLength = 32,
        };

        var btnOk = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(140, 92),
            Size = new Size(75, 30),
            BackColor = Color.FromArgb(21, 101, 192),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        var btnCancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(221, 92),
            Size = new Size(75, 30),
            FlatStyle = FlatStyle.Flat,
        };

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        Controls.Add(lbl);
        Controls.Add(_txtPassword);
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
    }

    /// <summary>用户输入的密码（DialogResult == OK 时有效）</summary>
    public string Password => _txtPassword.Text;
}
