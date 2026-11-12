using Core.Services;

namespace App.Services;

public interface IRegisterUiService
{
    Task<RegistrationOutcome> RegisterAsync(
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

    public Task<RegistrationOutcome> RegisterAsync(
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
            throw new RegistrationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7FF3F4D5719DE17C));

        // 2. 客户端校验（先校验，再委托）
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
            throw new RegistrationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA926A56E716ACDD6));

        if (string.IsNullOrEmpty(password))
            throw new RegistrationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3AA7A9E035A45598));

        if (password != confirm)
            throw new RegistrationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text58807745708ED726));

        if (name is not null && name.Trim().Length > 50)
            throw new RegistrationException(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text72DF0D02BD02C9B3));

        // 3. 委托到 IRegisterService
        return _service.RegisterAsync(serverUrl, email, name, password, hint, ct);
    }
}
