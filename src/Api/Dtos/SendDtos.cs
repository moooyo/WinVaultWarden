using System.Text.Json.Serialization;
using Core.Enums;

namespace Api.Dtos;

// /api/sends 列表响应:{ data:[...], object:"list", continuationToken:null }
public sealed record SendListResponse(
    [property: JsonPropertyName("data")] SendResponseDto[] Data,
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken = null);

// Send.to_json (db/models/send.rs:140)。text/file 二选一:
// text => { text, hidden };  file => { id, fileName, size(字符串), sizeName }。
public sealed record SendResponseDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accessId")] string AccessId,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("text")] SendTextDto? Text,
    [property: JsonPropertyName("file")] SendFileDto? File,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("maxAccessCount")] int? MaxAccessCount,
    [property: JsonPropertyName("accessCount")] int AccessCount,
    // Presence indicates password protection. The value is a server-side verifier and must never
    // be resubmitted as the password proof for a newly created Send.
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("authType")] int? AuthType,
    [property: JsonPropertyName("disabled")] bool Disabled,
    [property: JsonPropertyName("hideEmail")] bool HideEmail,
    [property: JsonPropertyName("revisionDate")] DateTimeOffset? RevisionDate,
    [property: JsonPropertyName("expirationDate")] DateTimeOffset? ExpirationDate,
    [property: JsonPropertyName("deletionDate")] DateTimeOffset DeletionDate,
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("emails")] string? Emails = null);

public sealed record SendTextDto(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("hidden")] bool Hidden);

// size 在服务端被转成字符串下发(mobile 兼容);AllowReadingFromString 让 long? 可解析。
public sealed record SendFileDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("sizeName")] string? SizeName);

// 写请求体,镜像 Vaultwarden SendData(src/api/core/sends.rs:72)。
// type=0 仅带 text；type=1 仅带 file + fileLength。deletionDate 必填且 <=31 天。
public sealed class SendRequest
{
    public int Type { get; init; }
    public string Key { get; init; } = string.Empty;
    // 密码"证明"(PBKDF2 base64),不是明文;为 null 表示不设密码。
    public string? Password { get; init; }
    public int? MaxAccessCount { get; init; }
    public string? ExpirationDate { get; init; }
    public string DeletionDate { get; init; } = string.Empty;
    public bool Disabled { get; init; }
    public bool? HideEmail { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public SendTextRequest? Text { get; init; }
    public SendFileRequest? File { get; init; }
    public int? FileLength { get; init; }
    public string? Id { get; init; }
}

public sealed record SendTextRequest(string? Text, bool Hidden);

public sealed record SendFileRequest(string FileName);

// POST /api/sends/file/v2 响应。
public sealed record SendFileUploadV2Response(
    [property: JsonPropertyName("fileUploadType")] FileUploadType FileUploadType,
    [property: JsonPropertyName("object")] string? Object,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sendResponse")] SendResponseDto SendResponse);

public sealed record FileUploadRenewalResponse(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("fileUploadType")] FileUploadType FileUploadType);

// POST /api/sends/access/{accessId} 请求体:{ password?:string }(密码证明)。
public sealed record SendAccessRequest(
    [property: JsonPropertyName("password")] string? Password);

public sealed record SendAccessTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("scope")] string? Scope);

public sealed record SendAccessTokenErrorResponse(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription,
    [property: JsonPropertyName("send_access_error_type")] string? SendAccessErrorType,
    [property: JsonPropertyName("message")] string? Message);

public abstract record SendAccessTokenResult
{
    private SendAccessTokenResult() { }

    public sealed record Success(string AccessToken, int ExpiresIn) : SendAccessTokenResult;
    public sealed record Unsupported : SendAccessTokenResult;
    public sealed record Error(
        int HttpStatus,
        string? ErrorCode,
        string? ErrorDescription,
        string? SendAccessErrorType) : SendAccessTokenResult;
}

// to_json_access(db/models/send.rs:173):无 key/password,附 creatorIdentifier。
public sealed record SendAccessResponseDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("text")] SendTextDto? Text,
    [property: JsonPropertyName("file")] SendFileDto? File,
    [property: JsonPropertyName("expirationDate")] DateTimeOffset? ExpirationDate,
    [property: JsonPropertyName("creatorIdentifier")] string? CreatorIdentifier,
    [property: JsonPropertyName("object")] string? Object);

// POST /sends/{sendId}/access/file/{fileId} 响应(sends.rs:post_access_file)。
// Vaultwarden 返回 { object:"send-fileDownload", id:fileId, url:signedUrl }。
// url 是带 JWT token 的临时下载地址,需 GET 拿到原始加密字节。
public sealed record SendFileDownloadResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("object")] string? Object);
