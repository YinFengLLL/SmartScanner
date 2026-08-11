namespace SmartScanner.Core.Security;

/// <summary>用户角色</summary>
public enum UserRole
{
    /// <summary>操作员：仅可操作与查看，参数设置区禁用</summary>
    Operator,

    /// <summary>管理员：参数设置区全部可编辑</summary>
    Admin,
}

/// <summary>
/// 用户权限服务（需求核心功能）：
/// 默认"操作员模式"（进程内不持久化角色，软件重启自动恢复操作员）。
/// 输入硬编码默认密码 admin123 校验通过后切换为"管理员模式"。
/// </summary>
public sealed class AuthService
{
    /// <summary>硬编码默认管理员密码（需求指定）</summary>
    public const string DefaultAdminPassword = "admin123";

    private UserRole _role = UserRole.Operator;

    /// <summary>当前角色</summary>
    public UserRole CurrentRole => _role;

    public bool IsAdmin => _role == UserRole.Admin;

    /// <summary>角色变化事件（切换用户按钮、登出时触发）</summary>
    public event Action<UserRole>? RoleChanged;

    /// <summary>用密码尝试登录管理员；密码正确返回 true 并切换角色，否则返回 false</summary>
    public bool TryLoginAsAdmin(string password)
    {
        if (!string.Equals(password, DefaultAdminPassword, StringComparison.Ordinal))
        {
            return false;
        }

        SetRole(UserRole.Admin);
        return true;
    }

    /// <summary>退回操作员模式（管理员点击"切换用户"时调用）</summary>
    public void Logout()
    {
        SetRole(UserRole.Operator);
    }

    /// <summary>状态栏显示名</summary>
    public string RoleDisplayName => IsAdmin ? "管理员" : "操作员";

    private void SetRole(UserRole role)
    {
        if (_role == role)
        {
            return;
        }

        _role = role;
        RoleChanged?.Invoke(role);
    }
}
