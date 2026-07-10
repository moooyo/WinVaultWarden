namespace Core.Models;

// Send 密码变更的显式三态,替代 null/空字符串暗号:
//   Keep   —— 不改动服务端已有密码(PUT 请求 password 字段为 null,服务端保留原值)。
//   Set    —— 设置/替换密码(携带新密码派生的证明)。
//   Remove —— 移除密码(元数据 PUT 成功后另调用专用 remove-password 路由)。
public abstract record SendPasswordChange
{
    private SendPasswordChange() { }

    public sealed record Keep : SendPasswordChange;

    public sealed record Set : SendPasswordChange
    {
        public Set(string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            Password = password;
        }

        public string Password { get; }
    }

    public sealed record Remove : SendPasswordChange;
}
