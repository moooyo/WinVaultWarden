namespace App.ViewModels.Models;

public enum SendType { Text, File }

public enum SendReconciliationState
{
    None,
    CommittedPendingRefresh,
    ReplacementOriginalPending,
    ReplacementNewPending,
}

public enum SendAvailabilityState
{
    Unknown,
    Available,
    Disabled,
    Expired,
    Deleted,
    AccessLimitReached,
    AwaitingConfirmation,
}

public readonly record struct SendAvailability(
    SendAvailabilityState State,
    DateTimeOffset? AvailableUntil)
{
    public bool IsAvailable => State == SendAvailabilityState.Available;
}

public sealed record SendListItem(
    string Id,
    string Name,
    SendType Type,
    string DeleteDate,
    string? Link,
    SendReconciliationState ReconciliationState = SendReconciliationState.None,
    bool IsReadOnly = false)
{
    public override string ToString() => Name;

    public DateTimeOffset? DeletionDateTime { get; init; }
    public DateTimeOffset? ExpirationDate { get; init; }
    public bool Disabled { get; init; }
    public int AccessCount { get; init; }
    public int? MaxAccessCount { get; init; }
    public long? FileSize { get; init; }
    public bool HasPassword { get; init; }
    public Core.Enums.SendAuthType AuthType { get; init; } = Core.Enums.SendAuthType.Unknown;
    public DateTimeOffset DisplayTime { get; init; } = DateTimeOffset.UtcNow;
    public string? TextContent { get; init; }
    public string? FileName { get; init; }
    public DateTimeOffset? RevisionDate { get; init; }
    public bool HasRowStatus => HasPassword || GetAvailability(DisplayTime).State is
        SendAvailabilityState.Disabled or SendAvailabilityState.Expired or SendAvailabilityState.Deleted
        or SendAvailabilityState.AccessLimitReached or SendAvailabilityState.AwaitingConfirmation;
    public string RowStatusGlyph => GetAvailability(DisplayTime).State switch
    {
        SendAvailabilityState.Disabled => "\uE733",
        SendAvailabilityState.Expired or SendAvailabilityState.Deleted or SendAvailabilityState.AccessLimitReached
            or SendAvailabilityState.AwaitingConfirmation => "\uE7BA",
        _ => "\uE72E",
    };
    public double NameOpacity => GetAvailability(DisplayTime).State is SendAvailabilityState.Available or SendAvailabilityState.Unknown ? 1 : 0.62;
    public string ToggleActionLabel => Disabled ? "启用" : "禁用";

    public string ListSubtitle => GetAvailability(DisplayTime).State switch
    {
        SendAvailabilityState.Disabled => "已禁用 · 链接暂时打不开",
        SendAvailabilityState.Expired or SendAvailabilityState.Deleted => "已过期",
        SendAvailabilityState.AccessLimitReached => "已达访问上限",
        SendAvailabilityState.AwaitingConfirmation => "等待服务器确认",
        _ => $"{DeletionRelativeLabel}删除 · 已访问 {AccessCount} 次",
    };

    public string DeletionRelativeLabel
    {
        get
        {
            if (DeletionDateTime is not { } deletion) return "待确认后";
            var remaining = deletion - DisplayTime;
            if (remaining <= TimeSpan.Zero) return "已到期 · ";
            if (remaining.TotalHours < 1) return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} 分钟后";
            if (remaining.TotalDays < 1) return $"{(int)Math.Ceiling(remaining.TotalHours)} 小时后";
            if (remaining.TotalDays < 2) return "明天";
            return $"{(int)Math.Ceiling(remaining.TotalDays)} 天后";
        }
    }

    public SendAvailability GetAvailability(DateTimeOffset now)
    {
        var availableUntil = ExpirationDate is { } expiration
            && (DeletionDateTime is null || expiration < DeletionDateTime)
                ? expiration : DeletionDateTime;
        var state = ReconciliationState != SendReconciliationState.None
            ? SendAvailabilityState.AwaitingConfirmation
            : DeletionDateTime <= now ? SendAvailabilityState.Deleted
            : Disabled ? SendAvailabilityState.Disabled
            : ExpirationDate <= now ? SendAvailabilityState.Expired
            : MaxAccessCount is { } limit && AccessCount >= limit ? SendAvailabilityState.AccessLimitReached
            : availableUntil is null ? SendAvailabilityState.Unknown
            : SendAvailabilityState.Available;
        return new SendAvailability(state, availableUntil);
    }

    public string AccessStatus => GetAvailability(DisplayTime).State switch
    {
        SendAvailabilityState.Available => "可访问",
        SendAvailabilityState.Disabled => "已停用",
        SendAvailabilityState.Expired => "已过期",
        SendAvailabilityState.Deleted => "已到删除时间",
        SendAvailabilityState.AccessLimitReached => "已达访问上限",
        SendAvailabilityState.AwaitingConfirmation => "等待服务器确认",
        _ => "状态待确认",
    };

    public string AudienceLabel => HasPassword || AuthType == Core.Enums.SendAuthType.Password
        ? "需要访问密码"
        : AuthType switch
        {
            Core.Enums.SendAuthType.None => "拥有链接的任何人",
            Core.Enums.SendAuthType.Email => "需要邮箱验证",
            _ => "访问方式待确认",
        };

    public string AvailableUntilLabel => GetAvailability(DisplayTime) is
        { IsAvailable: true, AvailableUntil: { } until }
            ? until.ToLocalTime().ToString("yyyy/M/d HH:mm")
            : "当前不可访问";

    public string RetentionLabel => DeletionDateTime is { } deletion
        ? $"{deletion.ToLocalTime():yyyy/M/d HH:mm} 删除"
        : DeleteDate;

    public string AvailableUntilAccessibleLabel =>
        $"{AccessStatus}，{AudienceLabel}，可访问至 {AvailableUntilLabel}，{RetentionLabel}";

    public string CompactAccessLabel => $"{AccessStatus} · {AudienceLabel}";

    public string MetadataLabel
    {
        get
        {
            var size = Type == SendType.File && FileSize is { } bytes
                ? $" · {FormatSize(bytes)}" : string.Empty;
            var access = MaxAccessCount is { } limit
                ? $"{AccessCount}/{limit} 次访问" : $"{AccessCount} 次访问";
            return $"{TypeLabel}{size} · {access}";
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:0.#} KiB",
        _ => $"{bytes / (1024d * 1024):0.#} MiB",
    };

    public string TypeLabel
    {
        get
        {
            var type = Type == SendType.File ? "文件" : "文本";
            return ReconciliationState switch
            {
                SendReconciliationState.CommittedPendingRefresh => $"{type} · 已保存，等待刷新",
                SendReconciliationState.ReplacementOriginalPending => $"{type} · 原 Send，删除结果待确认",
                SendReconciliationState.ReplacementNewPending => $"{type} · 新 Send，等待对账",
                _ => type,
            };
        }
    }

    public bool CanModify => !IsReadOnly && ReconciliationState == SendReconciliationState.None;

    public string DeleteDateAccessibleLabel => $"删除日期 {DeleteDate}";

    public string Glyph => Type switch
    {
        SendType.Text => "\uE8E4",
        SendType.File => "\uE8A5",
        _ => "\uE724",
    };
}

public sealed record SendDeleteRequest(
    SendListItem Item,
    Core.Session.VaultSessionLease OriginatingSession);
