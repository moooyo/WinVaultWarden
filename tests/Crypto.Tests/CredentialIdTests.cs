using Core.Passkeys;
using Xunit;

namespace Crypto.Tests;

public class CredentialIdTests
{
    private const string OfficialGuid = "08d70b74-e9f5-4522-a425-e5dcd40107e7";
    private const string OfficialBase64Url = "CNcLdOn1RSKkJeXc1AEH5w";

    private static readonly byte[] OfficialBytes =
    [
        0x08, 0xd7, 0x0b, 0x74, 0xe9, 0xf5, 0x45, 0x22,
        0xa4, 0x25, 0xe5, 0xdc, 0xd4, 0x01, 0x07, 0xe7,
    ];

    [Fact]
    public void DecodeStorage_OfficialGuidUsesNetworkByteOrder()
    {
        Assert.True(CredentialId.TryDecodeStorage(
            OfficialGuid,
            out var decoded,
            out var wasLegacy));

        Assert.False(wasLegacy);
        Assert.Equal(OfficialBytes, decoded);
    }

    [Fact]
    public void DecodeStorage_OfficialBase64PrefixDecodesRawBytes()
    {
        Assert.True(CredentialId.TryDecodeStorage(
            CredentialId.Base64Prefix + OfficialBase64Url,
            out var decoded,
            out var wasLegacy));

        Assert.False(wasLegacy);
        Assert.Equal(OfficialBytes, decoded);
    }

    [Fact]
    public void NormalizeStorage_LegacyCanonicalBase64UrlAddsPrefix()
    {
        Assert.True(CredentialId.TryDecodeStorage(
            OfficialBase64Url,
            out var decoded,
            out var wasLegacy));

        Assert.True(wasLegacy);
        Assert.Equal(OfficialBytes, decoded);
        Assert.Equal(
            CredentialId.Base64Prefix + OfficialBase64Url,
            CredentialId.NormalizeStorage(OfficialBase64Url));
    }

    [Fact]
    public void StorageIdsEqual_GuidBase64AndLegacyRepresentSameBytes()
    {
        var prefixed = CredentialId.Base64Prefix + OfficialBase64Url;

        Assert.True(CredentialId.StorageIdsEqual(OfficialGuid, prefixed));
        Assert.True(CredentialId.StorageIdsEqual(OfficialGuid, OfficialBase64Url));
        Assert.True(CredentialId.StorageIdEqualsWire(OfficialGuid, OfficialBase64Url));
    }

    [Fact]
    public void EncodeForStorage_ArbitraryBytesUsesOfficialBase64Prefix()
    {
        Assert.Equal("b64.AQIDBA", CredentialId.EncodeForStorage([1, 2, 3, 4]));
        Assert.Equal("AQIDBA", CredentialId.EncodeForWire([1, 2, 3, 4]));
    }

    [Fact]
    public void NormalizeStorage_UppercaseStandardGuidUsesCanonicalLowercase()
    {
        Assert.Equal(
            OfficialGuid,
            CredentialId.NormalizeStorage(OfficialGuid.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid")]
    [InlineData("b64.")]
    [InlineData("B64.AQIDBA")]
    [InlineData("b64.AQIDBA=")]
    [InlineData("b64.AQID+")]
    [InlineData("b64.AB")]
    [InlineData("{08d70b74-e9f5-4522-a425-e5dcd40107e7}")]
    [InlineData("08d70b74-e9f5-4522-a425-e5dcd40107e7 ")]
    public void TryDecodeStorage_RejectsMalformedValues(string? value)
    {
        Assert.False(CredentialId.TryDecodeStorage(value, out _, out _));
    }

    [Fact]
    public void GuidLikeCanonicalBase64UrlWithoutHyphensRemainsReadableAsLegacyData()
    {
        const string legacy = "08d70b74e9f54522a425e5dcd40107e7";

        Assert.True(CredentialId.TryDecodeStorage(legacy, out var bytes, out var wasLegacy));
        Assert.True(wasLegacy);
        Assert.Equal($"b64.{legacy}", CredentialId.NormalizeStorage(legacy));
        Assert.Equal(legacy, CredentialId.EncodeForWire(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("b64.AQIDBA")]
    [InlineData("AQIDBA=")]
    [InlineData("AQID+")]
    [InlineData("AB")]
    public void TryDecodeWire_RejectsNonCanonicalBase64Url(string? value)
    {
        Assert.False(CredentialId.TryDecodeWire(value, out _));
    }
}
