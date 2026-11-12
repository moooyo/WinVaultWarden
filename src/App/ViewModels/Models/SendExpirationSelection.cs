using System.Globalization;
using global::App.Localization;

namespace App.ViewModels.Models;

public enum SendDatePreset
{
    KeepExisting,
    Never,
    OneHour,
    OneDay,
    TwoDays,
    ThreeDays,
    SevenDays,
    ThirtyDays,
    Custom,
}

public sealed record SendDatePresetOption(SendDatePreset Value, string Label);

/// <summary>Builds presentation choices whose translated labels never identify the selected operation.</summary>
public static class SendDatePresetOptions
{
    public static IReadOnlyList<SendDatePresetOption> CreateDeletionOptions(
        bool includeKeepExisting = false,
        DateTimeOffset? existingDate = null,
        bool compact = false,
        Func<UiStringKey, string>? resolve = null,
        CultureInfo? culture = null)
    {
        var values = compact
            ? new[] { SendDatePreset.OneDay, SendDatePreset.SevenDays, SendDatePreset.ThirtyDays, SendDatePreset.Custom }
            : new[] { SendDatePreset.OneHour, SendDatePreset.OneDay, SendDatePreset.TwoDays,
                SendDatePreset.ThreeDays, SendDatePreset.SevenDays, SendDatePreset.ThirtyDays, SendDatePreset.Custom };
        return Create(values, includeKeepExisting, existingDate, relativeLabels: !compact,
            expiration: false, resolve, culture);
    }

    public static IReadOnlyList<SendDatePresetOption> CreateExpirationOptions(
        bool includeKeepExisting = false,
        DateTimeOffset? existingDate = null,
        bool compact = false,
        Func<UiStringKey, string>? resolve = null,
        CultureInfo? culture = null)
    {
        var values = compact
            ? new[] { SendDatePreset.Never, SendDatePreset.OneHour, SendDatePreset.OneDay,
                SendDatePreset.SevenDays, SendDatePreset.Custom }
            : new[] { SendDatePreset.Never, SendDatePreset.OneHour, SendDatePreset.OneDay,
                SendDatePreset.TwoDays, SendDatePreset.ThreeDays, SendDatePreset.SevenDays, SendDatePreset.Custom };
        return Create(values, includeKeepExisting, existingDate, relativeLabels: !compact,
            expiration: true, resolve, culture);
    }

    public static string DisplayLabel(
        SendDatePreset preset,
        bool relative = false,
        bool expiration = false,
        Func<UiStringKey, string>? resolve = null)
    {
        resolve ??= UiText.Get;
        var key = preset switch
        {
            SendDatePreset.Never => expiration && relative ? UiStringKey.Text2C60316D5EA6B49C : UiStringKey.Text3867F70AE6EFB579,
            SendDatePreset.OneHour => relative ? UiStringKey.Text84FDBE4301E85B69 : UiStringKey.Text5482D1ABC42F8A5B,
            SendDatePreset.OneDay => relative ? UiStringKey.Text005F9C9E2DEC364C : UiStringKey.Text30C21284DF4FEAC6,
            SendDatePreset.TwoDays => UiStringKey.Text6A6ADDDEFB062D6B,
            SendDatePreset.ThreeDays => UiStringKey.Text5FF022316E9B3FAB,
            SendDatePreset.SevenDays => relative ? UiStringKey.Text1CE16CF41F181338 : UiStringKey.Text38EEFACBB326E37F,
            SendDatePreset.ThirtyDays => relative ? UiStringKey.TextDF6B112991F3410F : UiStringKey.Text84AD2952A3089CE7,
            SendDatePreset.Custom => UiStringKey.Text4EAFA9E925B30BCD,
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "This preset needs date-specific presentation."),
        };
        return resolve(key);
    }

    public static DateTimeOffset? ResolveDate(
        SendDatePreset preset,
        DateTimeOffset now,
        DateTimeOffset? currentDate,
        int customDefaultDays) => preset switch
    {
        SendDatePreset.KeepExisting => currentDate,
        SendDatePreset.Never => null,
        SendDatePreset.Custom => currentDate ?? now.AddDays(customDefaultDays),
        _ => now + RelativeDuration(preset),
    };

    public static TimeSpan RelativeDuration(SendDatePreset preset) => preset switch
    {
        SendDatePreset.OneHour => TimeSpan.FromHours(1),
        SendDatePreset.OneDay => TimeSpan.FromDays(1),
        SendDatePreset.TwoDays => TimeSpan.FromDays(2),
        SendDatePreset.ThreeDays => TimeSpan.FromDays(3),
        SendDatePreset.SevenDays => TimeSpan.FromDays(7),
        SendDatePreset.ThirtyDays => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "The preset has no relative duration."),
    };

    private static IReadOnlyList<SendDatePresetOption> Create(
        IEnumerable<SendDatePreset> values,
        bool includeKeepExisting,
        DateTimeOffset? existingDate,
        bool relativeLabels,
        bool expiration,
        Func<UiStringKey, string>? resolve,
        CultureInfo? culture)
    {
        resolve ??= UiText.Get;
        culture ??= UiText.DisplayCulture;
        var choices = new List<SendDatePresetOption>();
        if (includeKeepExisting)
        {
            var date = existingDate?.ToLocalTime().ToString("g", culture) ?? string.Empty;
            choices.Add(new(SendDatePreset.KeepExisting,
                string.Format(culture, resolve(UiStringKey.Text78221BF887768C83), date)));
        }
        choices.AddRange(values.Select(value => new SendDatePresetOption(value,
            DisplayLabel(value, relativeLabels, expiration, resolve))));
        return choices;
    }
}

internal readonly record struct SendExpirationSelection(SendDatePreset Preset, DateTimeOffset? ExpirationDate)
{
    // These are legacy input identifiers, retained for existing callers rather than used as UI display values.
    public const string NeverLabel = "删除时";
    public const string OneHourLabel = "1 小时";
    public const string OneDayLabel = "1 天";
    public const string SevenDaysLabel = "7 天";
    public const string CustomLabel = "自定义";

    public string Label => SendDatePresetOptions.DisplayLabel(Preset, expiration: true);

    public static SendExpirationSelection FromExpiration(DateTimeOffset? expirationDate) =>
        new(expirationDate is null ? SendDatePreset.Never : SendDatePreset.Custom, expirationDate);

    public SendExpirationSelection Select(string label, DateTimeOffset now) => Select(LegacyPreset(label), now);

    public SendExpirationSelection Select(SendDatePreset preset, DateTimeOffset now) => preset switch
    {
        SendDatePreset.KeepExisting => this,
        _ => new(preset, SendDatePresetOptions.ResolveDate(preset, now, ExpirationDate, customDefaultDays: 1)),
    };

    internal static SendDatePreset LegacyPreset(string label) => label switch
    {
        NeverLabel => SendDatePreset.Never,
        OneHourLabel => SendDatePreset.OneHour,
        OneDayLabel => SendDatePreset.OneDay,
        SevenDaysLabel => SendDatePreset.SevenDays,
        "30 天" => SendDatePreset.ThirtyDays,
        CustomLabel => SendDatePreset.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Unknown legacy Send date identifier."),
    };

    public SendExpirationSelection SelectCustomDate(
        DateTimeOffset selectedDate,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);

        var current = ExpirationDate
            ?? throw new InvalidOperationException("A custom expiration must have an initial value.");
        var currentLocal = TimeZoneInfo.ConvertTime(current, localTimeZone);
        if (currentLocal.Date == selectedDate.Date)
            return this;

        var localDateTime = DateTime.SpecifyKind(
            selectedDate.Date + currentLocal.TimeOfDay,
            DateTimeKind.Unspecified);
        var offset = localTimeZone.GetUtcOffset(localDateTime);
        return new(SendDatePreset.Custom, new DateTimeOffset(localDateTime, offset));
    }
}
