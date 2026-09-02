namespace Core.Models;

public sealed record WebAuthnPublicKeyCredentialParameter(
    string Type,
    int Alg);

public sealed record WebAuthnCredentialDescriptor
{
    public WebAuthnCredentialDescriptor(
        string type,
        byte[] id,
        IReadOnlyList<string> transports)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(transports);

        Type = type;
        Id = id.ToArray();
        Transports = Array.AsReadOnly(transports.ToArray());
    }

    public string Type { get; }

    public byte[] Id { get; }

    public IReadOnlyList<string> Transports { get; }
}

public sealed class WebAuthnRegistrationChallenge
{
    private string? _boundAccountId;
    private long _boundGeneration;
    private ServerEndpoints? _boundEndpoints;
    private HashSet<int>? _existingCredentialIds;
    private int _bound;
    private int _consumed;

    public WebAuthnRegistrationChallenge(
        string challenge,
        string origin,
        string rpId,
        string rpName,
        byte[] userId,
        string userName,
        string userDisplayName,
        IReadOnlyList<WebAuthnPublicKeyCredentialParameter> publicKeyCredentialParameters,
        IReadOnlyList<WebAuthnCredentialDescriptor> excludeCredentials,
        int timeoutMilliseconds,
        string? authenticatorAttachment,
        string residentKey,
        bool requireResidentKey,
        string userVerification,
        string attestation,
        string? userVerificationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        ArgumentException.ThrowIfNullOrWhiteSpace(rpId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rpName);
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDisplayName);
        ArgumentNullException.ThrowIfNull(publicKeyCredentialParameters);
        ArgumentNullException.ThrowIfNull(excludeCredentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(residentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(userVerification);
        ArgumentException.ThrowIfNullOrWhiteSpace(attestation);

        Challenge = challenge;
        Origin = origin;
        RpId = rpId;
        RpName = rpName;
        UserId = userId.ToArray();
        UserName = userName;
        UserDisplayName = userDisplayName;
        PublicKeyCredentialParameters = Array.AsReadOnly(publicKeyCredentialParameters.ToArray());
        ExcludeCredentials = Array.AsReadOnly(excludeCredentials
            .Select(static descriptor => new WebAuthnCredentialDescriptor(
                descriptor.Type,
                descriptor.Id,
                descriptor.Transports))
            .ToArray());
        TimeoutMilliseconds = timeoutMilliseconds;
        AuthenticatorAttachment = authenticatorAttachment;
        ResidentKey = residentKey;
        RequireResidentKey = requireResidentKey;
        UserVerification = userVerification;
        Attestation = attestation;
        UserVerificationToken = string.IsNullOrWhiteSpace(userVerificationToken)
            ? null
            : userVerificationToken;
    }

    public string Challenge { get; }

    public string Origin { get; }

    public string RpId { get; }

    public string RpName { get; }

    public byte[] UserId { get; }

    public string UserName { get; }

    public string UserDisplayName { get; }

    public IReadOnlyList<WebAuthnPublicKeyCredentialParameter> PublicKeyCredentialParameters { get; }

    public IReadOnlyList<WebAuthnCredentialDescriptor> ExcludeCredentials { get; }

    public int TimeoutMilliseconds { get; }

    public string? AuthenticatorAttachment { get; }

    public string ResidentKey { get; }

    public bool RequireResidentKey { get; }

    public string UserVerification { get; }

    public string Attestation { get; }

    public string? UserVerificationToken { get; }

    public bool IsConsumed => Volatile.Read(ref _consumed) != 0;

    internal void BindTo(
        string accountId,
        long generation,
        ServerEndpoints endpoints,
        IEnumerable<int> existingCredentialIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(existingCredentialIds);
        if (Interlocked.CompareExchange(ref _bound, 1, 0) != 0)
            throw new InvalidOperationException("The WebAuthn registration challenge is already bound.");

        _boundAccountId = accountId;
        _boundGeneration = generation;
        _boundEndpoints = endpoints;
        _existingCredentialIds = existingCredentialIds.ToHashSet();
        Volatile.Write(ref _bound, 2);
    }

    internal bool IsBoundTo(string accountId, long generation, ServerEndpoints endpoints) =>
        Volatile.Read(ref _bound) == 2
        && string.Equals(_boundAccountId, accountId, StringComparison.Ordinal)
        && _boundGeneration == generation
        && _boundEndpoints == endpoints;

    internal bool CredentialIdExistedAtIssue(int id) =>
        _existingCredentialIds?.Contains(id) == true;

    internal bool TryConsume() =>
        Interlocked.CompareExchange(ref _consumed, 1, 0) == 0;
}

public sealed record WebAuthnRegistrationDeviceResponse
{
    public WebAuthnRegistrationDeviceResponse(
        byte[] credentialId,
        byte[] attestationObject,
        byte[] clientDataJson,
        IReadOnlyList<string> transports)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        ArgumentNullException.ThrowIfNull(attestationObject);
        ArgumentNullException.ThrowIfNull(clientDataJson);
        ArgumentNullException.ThrowIfNull(transports);

        CredentialId = credentialId.ToArray();
        AttestationObject = attestationObject.ToArray();
        ClientDataJson = clientDataJson.ToArray();
        Transports = Array.AsReadOnly(transports.ToArray());
    }

    public byte[] CredentialId { get; }

    public byte[] AttestationObject { get; }

    public byte[] ClientDataJson { get; }

    public IReadOnlyList<string> Transports { get; }
}
