namespace Api;

public interface IAuthenticatedApiClient :
    IReadonlyApiClient,
    IVaultWriteApiClient,
    ISendApiClient,
    IAttachmentApiClient,
    IAccountApiClient,
    ITwoFactorApiClient,
    IAuthRequestApiClient,
    IEmergencyAccessApiClient,
    IOriginBoundApiClient,
    IDisposable
{
}
