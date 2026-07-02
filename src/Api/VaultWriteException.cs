using System.Net;

namespace Api;

// 写操作被服务端拒绝(4xx)时抛出。派生自 VaultApiException(因此同时是 HttpRequestException),
// Message 为服务端返回的可读错误;富构造还携带状态码与字段级 validationErrors。
public sealed class VaultWriteException : VaultApiException
{
    // 服务端错误体的 validationErrors(字段名 -> 错误消息列表),camelCase 键;缺省为 null。
    public IReadOnlyDictionary<string, string[]>? ValidationErrors { get; }

    // 仅带消息(历史契约,现有测试与调用方沿用):状态码与 validationErrors 缺省。
    public VaultWriteException(string message)
        : base(null, message, message)
    {
    }

    // 富构造:携带状态码与字段级校验错误。
    public VaultWriteException(HttpStatusCode statusCode, string message, IReadOnlyDictionary<string, string[]>? validationErrors)
        : base(statusCode, message, message)
        => ValidationErrors = validationErrors;
}
