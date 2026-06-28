using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class SendListItemTests
{
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
}
