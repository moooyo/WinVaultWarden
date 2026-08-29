using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class SendExpirationSelectionTests
{
    [Fact]
    public void FromExpiration_WithoutValue_UsesNever()
    {
        var selection = SendExpirationSelection.FromExpiration(expirationDate: null);

        Assert.Equal(SendExpirationSelection.NeverLabel, selection.Label);
        Assert.Null(selection.ExpirationDate);
    }

    [Fact]
    public void FromExpiration_WithExistingValue_UsesCustomWithoutChangingInstant()
    {
        var expiration = DateTimeOffset.Parse("2026-09-03T14:25:36.1234567+02:00");

        var selection = SendExpirationSelection.FromExpiration(expiration);

        Assert.Equal(SendExpirationSelection.CustomLabel, selection.Label);
        Assert.Equal(expiration, selection.ExpirationDate);
    }

    [Fact]
    public void SelectCustom_WithExistingValue_PreservesExactInstant()
    {
        var expiration = DateTimeOffset.Parse("2026-09-03T14:25:36.1234567+02:00");
        var now = DateTimeOffset.Parse("2026-08-30T10:00:00Z");
        var selection = SendExpirationSelection.FromExpiration(expiration);

        var updated = selection.Select(SendExpirationSelection.CustomLabel, now);

        Assert.Equal(selection, updated);
        Assert.Equal(expiration, updated.ExpirationDate);
        Assert.Equal(expiration.ToString("O"), updated.ExpirationDate?.ToString("O"));
    }

    [Fact]
    public void SelectCustom_WithoutExistingValue_CreatesValidDefault()
    {
        var now = DateTimeOffset.Parse("2026-08-30T10:00:00Z");
        var selection = SendExpirationSelection.FromExpiration(expirationDate: null);

        var updated = selection.Select(SendExpirationSelection.CustomLabel, now);

        Assert.Equal(SendExpirationSelection.CustomLabel, updated.Label);
        Assert.Equal(now.AddDays(1), updated.ExpirationDate);
    }

    [Theory]
    [InlineData(SendExpirationSelection.OneHourLabel, 1)]
    [InlineData(SendExpirationSelection.OneDayLabel, 24)]
    [InlineData(SendExpirationSelection.SevenDaysLabel, 168)]
    public void SelectPreset_ReplacesValueRelativeToSelectionTime(string label, int hours)
    {
        var original = DateTimeOffset.Parse("2026-09-03T14:25:36+02:00");
        var now = DateTimeOffset.Parse("2026-08-30T10:00:00Z");
        var selection = SendExpirationSelection.FromExpiration(original);

        var updated = selection.Select(label, now);

        Assert.Equal(label, updated.Label);
        Assert.Equal(now.AddHours(hours), updated.ExpirationDate);
    }

    [Fact]
    public void SelectNever_ClearsExistingExpiration()
    {
        var expiration = DateTimeOffset.Parse("2026-09-03T14:25:36+02:00");
        var selection = SendExpirationSelection.FromExpiration(expiration);

        var updated = selection.Select(
            SendExpirationSelection.NeverLabel,
            DateTimeOffset.Parse("2026-08-30T10:00:00Z"));

        Assert.Equal(SendExpirationSelection.NeverLabel, updated.Label);
        Assert.Null(updated.ExpirationDate);
    }

    [Fact]
    public void SelectCustomDate_ChangesOnlyLocalCalendarDate()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08-test",
            TimeSpan.FromHours(8),
            "UTC+08-test",
            "UTC+08-test");
        var expiration = DateTimeOffset.Parse("2026-09-03T14:25:36.1234567+08:00");
        var selection = SendExpirationSelection.FromExpiration(expiration);
        var selectedDate = DateTimeOffset.Parse("2026-09-06T00:00:00+08:00");

        var updated = selection.SelectCustomDate(selectedDate, timeZone);

        Assert.Equal(SendExpirationSelection.CustomLabel, updated.Label);
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-06T14:25:36.1234567+08:00"),
            updated.ExpirationDate);
    }

    [Fact]
    public void SelectCustomDate_WithDisplayedDate_DoesNotNormalizeExistingOffsetOrPrecision()
    {
        var timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+08-same-date-test",
            TimeSpan.FromHours(8),
            "UTC+08-same-date-test",
            "UTC+08-same-date-test");
        var expiration = DateTimeOffset.Parse("2026-09-03T06:25:36.1234567Z");
        var selection = SendExpirationSelection.FromExpiration(expiration);
        var displayedDate = DateTimeOffset.Parse("2026-09-03T00:00:00+08:00");

        var updated = selection.SelectCustomDate(displayedDate, timeZone);

        Assert.Equal(selection, updated);
        Assert.Equal(expiration, updated.ExpirationDate);
        Assert.Equal(expiration.ToString("O"), updated.ExpirationDate?.ToString("O"));
    }
}
