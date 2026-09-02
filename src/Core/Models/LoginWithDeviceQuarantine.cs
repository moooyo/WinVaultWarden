namespace Core.Models;

/// <summary>
/// Identifies one account, complete server environment, and device for which a
/// device-login token exchange may have an uncertain outcome.
/// </summary>
public sealed record LoginWithDeviceQuarantineBinding
{
    public const int MaximumEndpointLength = 2048;

    public LoginWithDeviceQuarantineBinding(
        ServerEndpoints endpoints,
        string email,
        string deviceIdentifier)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (new[]
            {
                endpoints.WebVault.Value,
                endpoints.Api.Value,
                endpoints.Identity.Value,
                endpoints.Notifications.Value,
                endpoints.Icons.Value,
                endpoints.SendWeb.Value,
            }.Any(value => value.Length > MaximumEndpointLength))
        {
            throw new ArgumentException(
                $"Each server endpoint must not exceed {MaximumEndpointLength} characters.",
                nameof(endpoints));
        }
        var normalizedEmail = MasterPasswordSaltPolicy.NormalizeEmail(email);
        MasterPasswordSaltPolicy.Validate(normalizedEmail);

        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentifier);
        if (!Guid.TryParseExact(deviceIdentifier, "D", out var parsedDeviceIdentifier)
            || parsedDeviceIdentifier == Guid.Empty)
        {
            throw new ArgumentException(
                "The device identifier must be a non-empty GUID in D format.",
                nameof(deviceIdentifier));
        }

        Endpoints = endpoints;
        NormalizedEmail = normalizedEmail;
        DeviceIdentifier = parsedDeviceIdentifier.ToString("D");
    }

    public ServerEndpoints Endpoints { get; }

    public string NormalizedEmail { get; }

    public string DeviceIdentifier { get; }
}

/// <summary>
/// Durable non-secret evidence that a device-login request must remain quarantined
/// until its remote token-exchange outcome is known or explicitly cleared.
/// </summary>
public sealed record LoginWithDeviceQuarantineRecord
{
    public LoginWithDeviceQuarantineRecord(
        Guid entryId,
        long revision,
        LoginWithDeviceQuarantineBinding binding,
        string requestId,
        DateTimeOffset armedAt,
        DateTimeOffset requestExpiresAt,
        DateTimeOffset clearEligibleAt)
    {
        if (entryId == Guid.Empty)
            throw new ArgumentException("Entry identifier cannot be empty.", nameof(entryId));
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        if (!Guid.TryParseExact(requestId, "D", out var parsedRequestId)
            || parsedRequestId == Guid.Empty)
        {
            throw new ArgumentException(
                "The request identifier must be a non-empty GUID in D format.",
                nameof(requestId));
        }

        var normalizedArmedAt = armedAt.ToUniversalTime();
        var normalizedRequestExpiresAt = requestExpiresAt.ToUniversalTime();
        var normalizedClearEligibleAt = clearEligibleAt.ToUniversalTime();
        if (normalizedArmedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(armedAt),
                "The armed timestamp cannot precede the Unix epoch.");
        }
        if (normalizedRequestExpiresAt <= normalizedArmedAt)
        {
            throw new ArgumentException(
                "The request expiration must be later than the armed timestamp.",
                nameof(requestExpiresAt));
        }
        if (normalizedClearEligibleAt < normalizedRequestExpiresAt)
        {
            throw new ArgumentException(
                "The user-clear eligibility timestamp cannot precede request expiration.",
                nameof(clearEligibleAt));
        }

        EntryId = entryId;
        Revision = revision;
        Binding = binding;
        RequestId = parsedRequestId.ToString("D");
        ArmedAt = normalizedArmedAt;
        RequestExpiresAt = normalizedRequestExpiresAt;
        ClearEligibleAt = normalizedClearEligibleAt;
    }

    public Guid EntryId { get; }

    public long Revision { get; }

    public LoginWithDeviceQuarantineBinding Binding { get; }

    public string RequestId { get; }

    public DateTimeOffset ArmedAt { get; }

    public DateTimeOffset RequestExpiresAt { get; }

    public DateTimeOffset ClearEligibleAt { get; }

    public bool IsClearEligible(DateTimeOffset now) =>
        now.ToUniversalTime() >= ClearEligibleAt;
}
