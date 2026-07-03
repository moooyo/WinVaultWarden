using Crypto;
using Xunit;

namespace Crypto.Tests;

// 验证 SymmetricCryptoKey.Dispose 清零全部支撑数组(三种构造各一)。
public class SymmetricCryptoKeyDisposeTests
{
    [Fact]
    public void Dispose_32ByteCtor_ZerosFullKeyAndEncKey_MacKeyNull()
    {
        var bytes = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var key = new SymmetricCryptoKey(bytes);

        // 32 字节构造:FullKey 与 EncKey 别名同一数组,MacKey 为 null。
        Assert.Same(key.FullKey, key.EncKey);
        Assert.Null(key.MacKey);

        key.Dispose();

        Assert.All(key.FullKey, b => Assert.Equal(0, b));
        Assert.All(key.EncKey, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Dispose_64ByteCtor_ZerosFullKeyEncKeyMacKey()
    {
        var bytes = Enumerable.Range(1, 64).Select(i => (byte)i).ToArray();
        var key = new SymmetricCryptoKey(bytes);

        // 64 字节构造:EncKey/MacKey 是 FullKey 前后半的独立拷贝。
        Assert.NotNull(key.MacKey);
        Assert.Equal(64, key.FullKey.Length);
        Assert.Equal(32, key.EncKey.Length);
        Assert.Equal(32, key.MacKey!.Length);

        key.Dispose();

        Assert.All(key.FullKey, b => Assert.Equal(0, b));
        Assert.All(key.EncKey, b => Assert.Equal(0, b));
        Assert.All(key.MacKey!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Dispose_EncMacCtor_ZerosAllThreeArrays()
    {
        var enc = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var mac = Enumerable.Range(33, 32).Select(i => (byte)i).ToArray();
        var key = new SymmetricCryptoKey(enc, mac);

        // (enc,mac) 构造:EncKey/MacKey 别名输入,FullKey 是新拷贝。
        Assert.Same(enc, key.EncKey);
        Assert.Same(mac, key.MacKey);
        Assert.Equal(64, key.FullKey.Length);

        key.Dispose();

        Assert.All(key.FullKey, b => Assert.Equal(0, b));
        Assert.All(key.EncKey, b => Assert.Equal(0, b));
        Assert.All(key.MacKey!, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var key = new SymmetricCryptoKey(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray());

        key.Dispose();
        key.Dispose(); // 幂等:重复清零别名/已清零数组无害

        Assert.All(key.FullKey, b => Assert.Equal(0, b));
    }
}
