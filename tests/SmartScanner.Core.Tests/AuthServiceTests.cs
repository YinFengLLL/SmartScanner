using SmartScanner.Core.Security;

namespace SmartScanner.Core.Tests;

public class AuthServiceTests
{
    [Fact]
    public void 默认是操作员模式()
    {
        var auth = new AuthService();
        Assert.Equal(UserRole.Operator, auth.CurrentRole);
        Assert.False(auth.IsAdmin);
        Assert.Equal("操作员", auth.RoleDisplayName);
    }

    [Fact]
    public void 正确密码切换管理员()
    {
        var auth = new AuthService();
        Assert.True(auth.TryLoginAsAdmin(AuthService.DefaultAdminPassword));
        Assert.True(auth.IsAdmin);
        Assert.Equal(UserRole.Admin, auth.CurrentRole);
        Assert.Equal("管理员", auth.RoleDisplayName);
    }

    [Fact]
    public void 错误密码保持操作员()
    {
        var auth = new AuthService();
        Assert.False(auth.TryLoginAsAdmin("wrong-password"));
        Assert.Equal(UserRole.Operator, auth.CurrentRole);
    }

    [Fact]
    public void 登出退回操作员()
    {
        var auth = new AuthService();
        auth.TryLoginAsAdmin(AuthService.DefaultAdminPassword);
        auth.Logout();
        Assert.Equal(UserRole.Operator, auth.CurrentRole);
    }

    [Fact]
    public void 角色变化事件按序触发()
    {
        var auth = new AuthService();
        var roles = new List<UserRole>();
        auth.RoleChanged += r => roles.Add(r);

        auth.TryLoginAsAdmin(AuthService.DefaultAdminPassword);
        auth.Logout();

        Assert.Equal([UserRole.Admin, UserRole.Operator], roles);
    }
}
