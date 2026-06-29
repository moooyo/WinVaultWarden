namespace Core.Models;

// 已配置的两步验证提供者信息。Type 对应 Bitwarden TwoFactorProviderType 枚举值（服务端整数）。
public record TwoFactorProvider(int Type, bool Enabled);
