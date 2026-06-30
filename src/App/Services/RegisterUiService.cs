using Core.Services;

namespace App.Services;

public interface IRegisterUiService
{
    Task RegisterAsync(
        string serverUrl,
        string email,
        string? name,
        string password,
        string confirm,
        string? hint,
        CancellationToken ct = default);
}

public sealed class RegisterUiService : IRegisterUiService
{
    private readonly IRegisterService? _service;

    // 无参构造器：_service == null → RegisterAsync 抛出 RegistrationException
    public RegisterUiService()
    {
    }

    public RegisterUiService(IRegisterService? service)
    {
        _service = service;
    }

    public Task RegisterAsync(
        string serverUrl,
        string email,
        string? name,
        string password,
        string confirm,
        string? hint,
        CancellationToken ct = default)
    {
        // 1. 服务可用性检查
        if (_service is null)
            throw new RegistrationException("注册服务不可用");

        // 2. 客户端校验（先校验，再委托）
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
            throw new RegistrationException("请输入有效的邮箱地址");

        if (string.IsNullOrEmpty(password))
            throw new RegistrationException("密码不能为空");

        if (password != confirm)
            throw new RegistrationException("两次输入的密码不一致");

        if (name is not null && name.Trim().Length > 50)
            throw new RegistrationException("昵称不能超过 50 个字符");

        // 3. 委托到 IRegisterService
        return _service.RegisterAsync(serverUrl, email, name, password, hint, ct);
    }
}
