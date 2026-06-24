using System.Text;
using Crypto;
using Xunit;

namespace Crypto.Tests;

public class DecryptReadHelpersTests
{
    [Fact]
    public void DecryptToString_ReturnsUtf8Plaintext()
    {
        var service = new CryptoService();
        var key = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var enc = service.Encrypt(Encoding.UTF8.GetBytes("hello"), key).ToString();

        var text = service.DecryptToString(enc, key);

        Assert.Equal("hello", text);
    }

    [Fact]
    public void DecryptToString_NullOrWhitespace_ReturnsNull()
    {
        var service = new CryptoService();
        var key = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

        Assert.Null(service.DecryptToString(null, key));
        Assert.Null(service.DecryptToString("   ", key));
    }

    [Fact]
    public void DecryptItemKey_ReturnsSymmetricKeyFromEncryptedBytes()
    {
        var service = new CryptoService();
        var userKey = new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        var itemBytes = Enumerable.Range(64, 64).Select(i => (byte)i).ToArray();
        var encryptedItemKey = service.Encrypt(itemBytes, userKey).ToString();

        var itemKey = service.DecryptItemKey(encryptedItemKey, userKey);
        var enc = service.Encrypt(Encoding.UTF8.GetBytes("item"), itemKey).ToString();

        Assert.Equal("item", service.DecryptToString(enc, itemKey));
    }
}
