namespace Core.Models;

// 登录后用户档案的最小骨架。字段对应 /sync 的 profile。
public sealed class User
{
    public string Id { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Name { get; init; }
    // akey:用 MasterKey 包裹的 UserKey(EncString 文本)。
    public string Key { get; init; } = string.Empty;
    // 用 UserKey 加密的 RSA 私钥(EncString 文本)。
    public string? PrivateKey { get; init; }
}
