using System.Globalization;

namespace App.Services;

public static class AttachmentSizePolicy
{
    public static long MaxBytes => Core.Services.AttachmentLimits.MaxPlaintextBytes;

    public static bool IsWithinLimit(long sizeBytes) => sizeBytes <= MaxBytes;

    // 0..<1KB → "N B";<1MB → "N.N KB";否则 "N.N MB"(1 位小数、InvariantCulture、去尾零)。
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb.ToString("0.#", CultureInfo.InvariantCulture)} KB";
        double mb = kb / 1024.0;
        return $"{mb.ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }
}
