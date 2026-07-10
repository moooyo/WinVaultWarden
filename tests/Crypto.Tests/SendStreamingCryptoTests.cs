using System.Security.Cryptography;
using Core.Models;
using Crypto;
using Xunit;

namespace Crypto.Tests;

// Task 12：Send 文件体的两遍式常量内存流加密。核心不变式：
//   输出布局 = EncArrayBuffer[ (byte)2, IV(16), MAC(32), CT ]，MAC = HMAC-SHA256(macKey, IV‖CT)，
//   与 CryptoService.Encrypt/Decrypt 完全一致，故可用 DecryptBuffer 往返校验。
// 两遍：第一遍只算 MAC（不缓存密文），第二遍复用同一 IV/密钥重开源写出。可重开源被打开【恰好两次】。
public class SendStreamingCryptoTests
{
    private static SendCryptoService NewCrypto() => new(new CryptoService());
    private static SymmetricCryptoKey NewCryptoKey() => NewCrypto().DeriveCryptoKey(new byte[16]);

    private static SendFileSource Source(byte[] plaintext, Action onOpen, string name = "sample.bin") =>
        new(name, plaintext.LongLength, _ =>
        {
            onOpen();
            return ValueTask.FromResult<Stream>(new MemoryStream(plaintext, writable: false));
        });

    [Fact]
    public void FileLength_Exactly500MiBAccepted_OverLimitRejected()
    {
        SendFileSource.ValidateLength(500L * 1024 * 1024);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SendFileSource.ValidateLength(500L * 1024 * 1024 + 1));
        Assert.Equal("length", ex.ParamName);
    }

    [Fact]
    public void FileLength_Negative_Rejected()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => SendFileSource.ValidateLength(-1));
        Assert.Equal("length", ex.ParamName);
    }

    [Fact]
    public async Task PreparedStream_ReopensTwice_AndDecryptsToOriginal()
    {
        var plaintext = Enumerable.Range(0, 131_089).Select(i => (byte)(i % 251)).ToArray();
        var opens = 0;
        var source = Source(plaintext, () => opens++);
        using var key = NewCryptoKey();
        var crypto = NewCrypto();

        var prepared = await crypto.PrepareFileUploadAsync(source, key, CancellationToken.None);
        await using var output = new MemoryStream();
        await prepared.WriteAsync(output, CancellationToken.None);

        Assert.Equal(2, opens);
        Assert.Equal(prepared.Length, output.Length);
        Assert.Equal(plaintext, crypto.DecryptBuffer(output.ToArray(), key));
    }

    [Fact]
    public async Task PreparedStream_LayoutIsEncArrayBufferWithVersion2Header()
    {
        var plaintext = RandomNumberGenerator.GetBytes(1000);
        using var key = NewCryptoKey();
        var crypto = NewCrypto();

        var prepared = await crypto.PrepareFileUploadAsync(
            Source(plaintext, () => { }), key, CancellationToken.None);
        await using var output = new MemoryStream();
        await prepared.WriteAsync(output, CancellationToken.None);
        var bytes = output.ToArray();

        // 1(header)+16(iv)+32(mac)+PKCS7 密文长度(向上取整到下一个块)。
        var expected = 49L + ((plaintext.LongLength / 16L) + 1L) * 16L;
        Assert.Equal(expected, prepared.Length);
        Assert.Equal(expected, bytes.LongLength);
        Assert.Equal((byte)2, bytes[0]);
    }

    [Fact]
    public async Task PreparedStream_EmptyFile_RoundTripsToEmpty()
    {
        using var key = NewCryptoKey();
        var crypto = NewCrypto();

        var prepared = await crypto.PrepareFileUploadAsync(
            Source(Array.Empty<byte>(), () => { }), key, CancellationToken.None);
        await using var output = new MemoryStream();
        await prepared.WriteAsync(output, CancellationToken.None);

        Assert.Equal(49L + 16L, prepared.Length); // 空明文 → 一个 PKCS7 填充块
        Assert.Equal(prepared.Length, output.Length);
        Assert.Empty(crypto.DecryptBuffer(output.ToArray(), key));
    }

    [Fact]
    public async Task PrepareFileUpload_OverLimit_Throws_BeforeOpeningStream()
    {
        var opens = 0;
        var source = new SendFileSource("too-large.bin", 500L * 1024 * 1024 + 1, _ =>
        {
            opens++;
            return ValueTask.FromResult<Stream>(Stream.Null);
        });
        using var key = NewCryptoKey();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NewCrypto().PrepareFileUploadAsync(source, key, CancellationToken.None));
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task PrepareFileUpload_KeyWithoutMac_Throws()
    {
        using var encOnly = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAsync<CryptographicException>(() =>
            NewCrypto().PrepareFileUploadAsync(
                Source(new byte[] { 1, 2, 3 }, () => { }), encOnly, CancellationToken.None));
    }

    [Fact]
    public async Task PreparedStream_SameLengthContentChangedBetweenPasses_FailsClosed()
    {
        // 安全门禁(Vault Task 12 复审):两遍式流加密第一遍算 MAC、第二遍写密文。若文件在两遍之间被改成
        // 【相同长度但不同内容】,仅靠长度校验无法发现,会写出 MAC 与密文不匹配的损坏 Send(fail-open)。
        // 必须 fail-closed:第二遍以同一 IV/密钥重算 HMAC 并与第一遍比对,不一致即抛出、拒绝上传。
        var first = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var second = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }; // 同长度,不同内容
        var opens = 0;
        var source = new SendFileSource("mutating.bin", first.LongLength, _ =>
        {
            opens++;
            var payload = opens == 1 ? first : second;
            return ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false));
        });
        using var key = NewCryptoKey();
        var crypto = NewCrypto();

        var prepared = await crypto.PrepareFileUploadAsync(source, key, CancellationToken.None);
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            prepared.WriteAsync(output, CancellationToken.None));
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task PreparedStream_SourceGrewAfterSelection_Throws()
    {
        // 声明长度小于实际内容 → 第一遍算 MAC 时即侦测到源在选择后变长，拒绝上传。
        var actual = new byte[64];
        var source = new SendFileSource("grew.bin", 32, _ =>
            ValueTask.FromResult<Stream>(new MemoryStream(actual, writable: false)));
        using var key = NewCryptoKey();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            var prepared = await NewCrypto().PrepareFileUploadAsync(source, key, CancellationToken.None);
            await using var output = new MemoryStream();
            await prepared.WriteAsync(output, CancellationToken.None);
        });
    }
}
