using App.Services;
using Xunit;

namespace App.Tests;

public class AttachmentSizePolicyTests
{
    [Fact] public void MaxBytes_Is100MB() => Assert.Equal(100L * 1024 * 1024, AttachmentSizePolicy.MaxBytes);

    [Theory]
    [InlineData(0, true)]
    [InlineData(104857600, true)]          // exactly 100MB
    [InlineData(104857601, false)]         // 100MB + 1
    public void IsWithinLimit_Boundary(long size, bool expected) =>
        Assert.Equal(expected, AttachmentSizePolicy.IsWithinLimit(size));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(104857600, "100 MB")]
    public void FormatSize_Buckets(long bytes, string expected) =>
        Assert.Equal(expected, AttachmentSizePolicy.FormatSize(bytes));
}
