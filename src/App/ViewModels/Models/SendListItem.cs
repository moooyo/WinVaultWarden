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
    public string ToggleActionLabel => Disabled ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF4F0EAD1116B5B62) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7DF5C456C765E4C3);

    public string ListSubtitle => GetAvailability(DisplayTime).State switch
    {
        SendAvailabilityState.Disabled => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text82DCBAF77DBDF9E8),
        SendAvailabilityState.Expired or SendAvailabilityState.Deleted => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2FE0E3339AC4D1BB),
        SendAvailabilityState.AccessLimitReached => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text91195D91235E3EC1),
        SendAvailabilityState.AwaitingConfirmation => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDBA89EC86A14EB9E),
        _ => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text2A700181943DAFA3,DeletionRelativeLabel,AccessCount),
    };

    public string DeletionRelativeLabel
    {
        get
        {
            if (DeletionDateTime is not { } deletion) return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4A2ADA2CDA37C42C);
            var remaining = deletion - DisplayTime;
            if (remaining <= TimeSpan.Zero) return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDC80D44EFFAC8101);
            if (remaining.TotalHours < 1) return global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text79BD1453F811E2F5,Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
            if (remaining.TotalDays < 1) return global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text480F29736393C1B6,(int)Math.Ceiling(remaining.TotalHours));
            if (remaining.TotalDays < 2) return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB1D841B978FB524C);
            return global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text509E198F04F98C5A,(int)Math.Ceiling(remaining.TotalDays));
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
        SendAvailabilityState.Available => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E07E4EAF02DB9D1),
        SendAvailabilityState.Disabled => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA8C3698B5B8C485D),
        SendAvailabilityState.Expired => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2FE0E3339AC4D1BB),
        SendAvailabilityState.Deleted => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDCB9E19FE323DCD1),
        SendAvailabilityState.AccessLimitReached => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text91195D91235E3EC1),
        SendAvailabilityState.AwaitingConfirmation => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDBA89EC86A14EB9E),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCDEA037991951159),
    };

    public string AudienceLabel => HasPassword || AuthType == Core.Enums.SendAuthType.Password
        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC3B64BFB0029F21B)
        : AuthType switch
        {
            Core.Enums.SendAuthType.None => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7BE417BF2D4937D9),
            Core.Enums.SendAuthType.Email => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1CDF9DFD62D35990),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text02DADFD72BDAE282),
        };

    public string AvailableUntilLabel => GetAvailability(DisplayTime) is
        { IsAvailable: true, AvailableUntil: { } until }
            ? until.ToLocalTime().ToString("yyyy/M/d HH:mm")
            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text36CE943FECDCB1AD);

    public string RetentionLabel => DeletionDateTime is { } deletion
        ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextB15B9532A37ACAE6,deletion.ToLocalTime())
        : DeleteDate;

    public string AvailableUntilAccessibleLabel =>
        global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextFDBD912EBC54DC98,AccessStatus,AudienceLabel,AvailableUntilLabel,RetentionLabel);

    public string CompactAccessLabel => $"{AccessStatus} · {AudienceLabel}";

    public string MetadataLabel
    {
        get
        {
            var size = Type == SendType.File && FileSize is { } bytes
                ? $" · {FormatSize(bytes)}" : string.Empty;
            var access = MaxAccessCount is { } limit
                ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextC2BE1E4BFFDB2B25,AccessCount,limit) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextEBB83E175081AD1A,AccessCount);
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
            var type = Type == SendType.File ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text39932F24FE11A6BA) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDD81861811E57BF7);
            return ReconciliationState switch
            {
                SendReconciliationState.CommittedPendingRefresh => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextC11AE41771057E3B,type),
                SendReconciliationState.ReplacementOriginalPending => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextDBB36A9F9943D106,type),
                SendReconciliationState.ReplacementNewPending => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextA94571EDEB6295BF,type),
                _ => type,
            };
        }
    }

    public bool CanModify => !IsReadOnly && ReconciliationState == SendReconciliationState.None;

    public string DeleteDateAccessibleLabel => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextF7580DCE41F82E58,DeleteDate);

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
