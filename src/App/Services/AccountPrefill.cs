namespace App.Services;

/// <summary>
/// 注册成功 → 登录页 的一次性预填暂存（DI 单例）。RegisterPage 设置服务器/邮箱,
/// 下一个构造的 LoginViewModel 取用并清空(LoginViewModel 是 Transient,注册页直接在
/// 旧实例上 PrepareAccount 会被登录页新建的实例丢弃,R10)。用单例而非静态以避免并行测试污染。
/// </summary>
public sealed class AccountPrefill
{
    private string? _serverUrl;
    private string? _email;

    public void Set(string serverUrl, string email)
    {
        _serverUrl = serverUrl;
        _email = email;
    }

    /// <summary>取用并清空。有值返回 (server, email),否则 null。</summary>
    public (string ServerUrl, string Email)? Take()
    {
        if (_serverUrl is null || _email is null)
            return null;
        var result = (_serverUrl, _email);
        _serverUrl = null;
        _email = null;
        return result;
    }
}
