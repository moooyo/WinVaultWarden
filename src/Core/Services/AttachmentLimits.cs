namespace Core.Services;

// 附件明文上限的单一真源;Vault 强制、App 前置校验共用。
public static class AttachmentLimits
{
    public const long MaxPlaintextBytes = 100L * 1024 * 1024; // 100 MB
}
