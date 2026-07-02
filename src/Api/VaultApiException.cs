using System.Net;

namespace Api;

// Api 层统一的服务端错误异常。
// 关键设计:派生自 HttpRequestException —— 所有既有的 `is HttpRequestException` /
// `catch (HttpRequestException)` 处理器无需改动即继续生效,异常只是额外携带了
// 服务端返回的可读消息(ServerMessage)与状态码(StatusCode)。
public class VaultApiException : HttpRequestException
{
    // 服务端错误体中的 message 字段(若能解析);非 JSON 错误体时为 null。
    public string? ServerMessage { get; }

    public VaultApiException(HttpStatusCode? statusCode, string? serverMessage, string message, Exception? inner = null)
        : base(message, inner, statusCode)
        => ServerMessage = serverMessage;
}
