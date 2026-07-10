using Api;
using Xunit;

namespace Api.Tests;

public sealed class AccessTokenSubjectReaderTests
{
    [Fact]
    public void TryReadAccountId_ReadsNonEmptySub()
    {
        var token = Jwt("{\"sub\":\"user-42\",\"email\":\"me@example.com\"}");
        Assert.True(AccessTokenSubjectReader.TryReadAccountId(token, out var accountId));
        Assert.Equal("user-42", accountId);
    }

    [Theory]
    [InlineData("opaque-token")]
    [InlineData("e30.e30.signature")]
    [InlineData("e30.bm90LWpzb24.signature")]
    public void TryReadAccountId_RejectsMalformedOrMissingSub(string token)
    {
        Assert.False(AccessTokenSubjectReader.TryReadAccountId(token, out var accountId));
        Assert.Equal(string.Empty, accountId);
    }

    private static string Jwt(string payload) =>
        $"e30.{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.signature";
}
