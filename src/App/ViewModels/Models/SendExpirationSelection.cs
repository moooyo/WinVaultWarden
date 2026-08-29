namespace App.ViewModels.Models;

internal readonly record struct SendExpirationSelection(string Label, DateTimeOffset? ExpirationDate)
{
    public const string NeverLabel = "永不";
    public const string OneHourLabel = "1 小时";
    public const string OneDayLabel = "1 天";
    public const string SevenDaysLabel = "7 天";
    public const string CustomLabel = "自定义";

    public static SendExpirationSelection FromExpiration(DateTimeOffset? expirationDate) =>
        new(expirationDate is null ? NeverLabel : CustomLabel, expirationDate);

    public SendExpirationSelection Select(string label, DateTimeOffset now) => label switch
    {
        NeverLabel => new(NeverLabel, null),
        OneHourLabel => new(OneHourLabel, now.AddHours(1)),
        OneDayLabel => new(OneDayLabel, now.AddDays(1)),
        SevenDaysLabel => new(SevenDaysLabel, now.AddDays(7)),
        CustomLabel => new(CustomLabel, ExpirationDate ?? now.AddDays(1)),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Unknown Send expiration selection."),
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
        return new(CustomLabel, new DateTimeOffset(localDateTime, offset));
    }
}
