namespace Core.Passkeys;

// Stable, code-only wire error identifiers shared by the browser bridge, the native
// messaging host and the extension. Values are the on-wire contract and MUST NOT change;
// protocol/passkey-errors.json maps every code to its DOMException / fallback behaviour.
public static class PasskeyErrorCodes
{
    public const string ClientNotHandled = "client_not_handled";
    public const string UserCancelled = "user_cancelled";
    public const string UserVerificationFailed = "user_verification_failed";
    public const string DeadlineExceeded = "deadline_exceeded";
    public const string RateLimited = "rate_limited";
    public const string RequestInProgress = "request_in_progress";
    public const string CredentialUnusable = "credential_unusable";
    public const string CredentialExcluded = "credential_excluded";
    public const string UnsupportedOperation = "unsupported_operation";
    public const string UnsupportedAlgorithm = "unsupported_algorithm";
    public const string SecurityInsecureOrigin = "security_insecure_origin";
    public const string SecurityInvalidRp = "security_invalid_rp";
    public const string SecurityPublicSuffix = "security_public_suffix";
    public const string SecurityIdn = "security_idn";
    public const string SecurityFrame = "security_frame";
    public const string SecurityPermissionsPolicy = "security_permissions_policy";
    public const string SecuritySender = "security_sender";
    public const string OperationAborted = "operation_aborted";
    public const string InternalError = "internal_error";
}
