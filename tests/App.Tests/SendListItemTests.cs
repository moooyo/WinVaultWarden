using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class SendListItemTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-16T08:00:00Z");

    [Fact]
    public void StringRepresentation_ExposesOnlyNameAndNeverShareLinkOrKeyFragment()
    {
        var item = new SendListItem(
            "private-send-id",
            "Review handoff",
            SendType.Text,
            "2026-09-23",
            "https://vault.example/#/send/private-access-id/private-key-fragment")
        {
            HasPassword = true,
            MaxAccessCount = 5,
        };

        var representation = item.ToString();

        Assert.Equal("Review handoff", representation);
        Assert.DoesNotContain(item.Link!, representation, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-fragment", representation, StringComparison.Ordinal);
        Assert.DoesNotContain(item.Id, representation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 7, 7)]
    [InlineData(2, 7, 2)]
    [InlineData(14, 7, 7)]
    public void Availability_StopsAtEarlierExpirationOrDeletion(int? expirationDays, int deletionDays, int expectedDays)
    {
        var item = ActiveItem() with
        {
            ExpirationDate = expirationDays is { } days ? Now.AddDays(days) : null,
            DeletionDateTime = Now.AddDays(deletionDays),
        };

        var availability = item.GetAvailability(Now);

        Assert.True(availability.IsAvailable);
        Assert.Equal(Now.AddDays(expectedDays), availability.AvailableUntil);
    }

    [Fact]
    public void DisabledSend_RetainsCopyAndEditButStopsAccess()
    {
        var item = ActiveItem() with { Disabled = true };

        Assert.Equal(SendAvailabilityState.Disabled, item.GetAvailability(Now).State);
        Assert.True(item.CanModify);
        Assert.NotNull(item.Link);
        Assert.Equal(Now.AddDays(7), item.GetAvailability(Now).AvailableUntil);
    }

    [Theory]
    [InlineData(0, 0, SendAvailabilityState.AccessLimitReached)]
    [InlineData(1, 1, SendAvailabilityState.AccessLimitReached)]
    [InlineData(2, 1, SendAvailabilityState.Available)]
    [InlineData(null, 10, SendAvailabilityState.Available)]
    public void Availability_RespectsAccessLimitIncludingZero(int? limit, int accessCount, SendAvailabilityState expected)
    {
        var item = ActiveItem() with { MaxAccessCount = limit, AccessCount = accessCount };

        Assert.Equal(expected, item.GetAvailability(Now).State);
    }

    [Fact]
    public void Availability_ExpiresAtExactBoundaryWithoutChangingRetention()
    {
        var item = ActiveItem() with { ExpirationDate = Now.AddHours(1) };

        Assert.True(item.GetAvailability(Now.AddHours(1).AddTicks(-1)).IsAvailable);
        Assert.Equal(SendAvailabilityState.Expired, item.GetAvailability(Now.AddHours(1)).State);
        Assert.Equal(SendAvailabilityState.Deleted, item.GetAvailability(Now.AddDays(7)).State);
        Assert.Equal(Now.AddDays(7), item.DeletionDateTime);
    }

    [Fact]
    public void Availability_PendingSnapshotNeverClaimsToBeAvailable()
    {
        var item = ActiveItem() with { ReconciliationState = SendReconciliationState.CommittedPendingRefresh };

        Assert.Equal(SendAvailabilityState.AwaitingConfirmation, item.GetAvailability(Now).State);
        Assert.False(item.CanModify);
    }

    private static SendListItem ActiveItem() => new("send", "Review", SendType.Text, "2026-09-23", "https://vault.example/#/send/id/key")
    {
        DeletionDateTime = Now.AddDays(7),
        AuthType = Core.Enums.SendAuthType.None,
    };

    [Fact]
    public void DeleteDateAccessibleLabel_PrefixesDeleteDate()
    {
        var item = new SendListItem("id-1", "My Send", SendType.Text, "2026-07-01", "https://send/abc");

        Assert.Equal("删除日期 2026-07-01", item.DeleteDateAccessibleLabel);
    }

    [Fact]
    public void DeleteDateAccessibleLabel_HandlesEmptyDate()
    {
        var item = new SendListItem("id-2", "No Date", SendType.File, "", null);

        Assert.Equal("删除日期 ", item.DeleteDateAccessibleLabel);
    }

    [Fact]
    public void OfflineItem_CannotBeModified()
    {
        var item = new SendListItem(
            "id-offline",
            "Cached",
            SendType.Text,
            "later",
            null,
            IsReadOnly: true);

        Assert.False(item.CanModify);
    }
}
