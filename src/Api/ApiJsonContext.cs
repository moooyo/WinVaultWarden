using System.Text.Json.Serialization;
using Api.Dtos;

namespace Api;

// 镜像 ApiClient 历史使用的 new JsonSerializerOptions(JsonSerializerDefaults.Web):
// camelCase + 大小写不敏感读 + 数字可从字符串读;不忽略 null(Web 默认写出 null)。
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(PreloginRequest))]
[JsonSerializable(typeof(PreloginResponse))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(ConnectTokenErrorResponse))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(ListResponse<DeviceDto>))]
[JsonSerializable(typeof(WriteErrorResponse))]
[JsonSerializable(typeof(CipherRequest))]
[JsonSerializable(typeof(FolderRequest))]
[JsonSerializable(typeof(FolderDto))]
[JsonSerializable(typeof(SendListResponse))]
[JsonSerializable(typeof(SendResponseDto))]
[JsonSerializable(typeof(SendRequest))]
[JsonSerializable(typeof(SendFileUploadV2Response))]
[JsonSerializable(typeof(SendAccessRequest))]
[JsonSerializable(typeof(SendAccessResponseDto))]
[JsonSerializable(typeof(SendFileDownloadResponse))]
[JsonSerializable(typeof(CipherDto))]
[JsonSerializable(typeof(CipherAttachmentDto))]
[JsonSerializable(typeof(AttachmentUploadRequest))]
[JsonSerializable(typeof(AttachmentUploadV2Response))]
[JsonSerializable(typeof(ProfileUpdateRequest))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(ChangeKdfRequest))]
[JsonSerializable(typeof(PasswordVerifyRequest))]
[JsonSerializable(typeof(AuthenticatorResponse))]
[JsonSerializable(typeof(EnableAuthenticatorRequest))]
[JsonSerializable(typeof(DisableTwoFactorRequest))]
[JsonSerializable(typeof(DisableAuthenticatorRequest))]
[JsonSerializable(typeof(EmailStatusResponse))]
[JsonSerializable(typeof(SendEmailRequest))]
[JsonSerializable(typeof(EmailVerifyRequest))]
[JsonSerializable(typeof(RecoverResponse))]
[JsonSerializable(typeof(TwoFactorProviderItem))]
[JsonSerializable(typeof(List<TwoFactorProviderItem>))]
[JsonSerializable(typeof(TwoFactorProvidersResponse))]
[JsonSerializable(typeof(AuthRequestResponse))]
[JsonSerializable(typeof(List<AuthRequestResponse>))]
[JsonSerializable(typeof(AuthRequestListResponse))]
[JsonSerializable(typeof(AuthResponseRequest))]
[JsonSerializable(typeof(AuthRequestRequest))]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(CipherIdsRequest))]
[JsonSerializable(typeof(MoveCiphersRequest))]
public partial class ApiJsonContext : JsonSerializerContext;
