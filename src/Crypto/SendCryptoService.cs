using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Core.Models;

namespace Crypto;

// Bitwarden Send 加密链。与 bitwarden/clients 实现逐位对齐。
// seed(16B) --HKDF(bitwarden-send/send,64B)--> cryptoKey(enc32+mac32)
// 字段用 cryptoKey 加密;seed 本身用 userKey 包裹放入 key 字段。
public sealed class SendCryptoService
{
    private const int SeedLength = 16;
    private const int SendKdfIterations = 100_000;
    private static readonly byte[] HkdfSalt = "bitwarden-send"u8.ToArray();
    private static readonly byte[] HkdfInfo = "send"u8.ToArray();
    private const string ShareUrlMarker = "/#/send/";

    private readonly CryptoService _crypto;

    public SendCryptoService(CryptoService crypto) => _crypto = crypto;

    // 16 字节随机种子。放进分享 URL 片段,并用 userKey 包裹后作为 key 字段。
    public byte[] GenerateSeed() => RandomNumberGenerator.GetBytes(SeedLength);

    // 完整 HKDF(extract+expand)SHA256:salt="bitwarden-send",info="send",输出 64B。
    // 拆为 enc(32)+mac(32) 的对称密钥。
    public SymmetricCryptoKey DeriveCryptoKey(byte[] seed)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: seed,
            outputLength: 64,
            salt: HkdfSalt,
            info: HkdfInfo);
        return new SymmetricCryptoKey(key);
    }

    // key 字段 = Encrypt(seed, userKey) 的 EncString 字符串(type 2)。
    public string WrapSeed(byte[] seed, SymmetricCryptoKey userKey)
        => _crypto.Encrypt(seed, userKey).ToString();

    public byte[] UnwrapSeed(string encKey, SymmetricCryptoKey userKey)
        => _crypto.Decrypt(EncString.Parse(encKey), userKey);

    // name/notes/text/fileName 等字段 = Encrypt(utf8, cryptoKey) 的 EncString 字符串。
    public string EncryptField(string plaintext, SymmetricCryptoKey cryptoKey)
        => _crypto.Encrypt(Encoding.UTF8.GetBytes(plaintext), cryptoKey).ToString();

    public string? DecryptField(string? encText, SymmetricCryptoKey cryptoKey)
    {
        if (string.IsNullOrEmpty(encText))
            return null;
        var bytes = _crypto.Decrypt(EncString.Parse(encText), cryptoKey);
        return Encoding.UTF8.GetString(bytes);
    }

    // 文件体 = Bitwarden EncArrayBuffer 二进制:concat[ (byte)2, iv(16), mac(32), ct ]。
    public byte[] EncryptToBuffer(byte[] plaintext, SymmetricCryptoKey cryptoKey)
        => EncArrayBuffer.Pack(_crypto.Encrypt(plaintext, cryptoKey));

    // 解析 EncArrayBuffer:byte0(=2),iv=[1..17],mac=[17..49],ct=[49..]。
    public byte[] DecryptBuffer(byte[] buffer, SymmetricCryptoKey cryptoKey)
        => _crypto.Decrypt(EncArrayBuffer.Unpack(buffer), cryptoKey);

    // 两遍式常量内存流加密,产出与 EncryptToBuffer 逐字节一致的 EncArrayBuffer,但绝不把明文/密文整份载入内存。
    // 布局 = [ (byte)2, iv(16), mac(32), ct ],mac = HMAC-SHA256(macKey, iv‖ct),与 CryptoService.Encrypt/Decrypt 一致。
    // 因 MAC 位于密文之前,而计算 MAC 需读完全部密文,故必须两遍:
    //   第一遍(ComputeFileMacAsync):重开源、以固定 iv/密钥做 AES-CBC 加密并把密文喂给 IncrementalHash,只得到 MAC;
    //   第二遍(WriteAsync 委托):重开源、复用【同一】iv/密钥,把 [2, iv, mac, ct] 直接写进目标流。
    // AES-CBC + PKCS7 在相同 iv/密钥/明文下确定,两遍密文必然一致,故第一遍算出的 MAC 可鉴别第二遍写出的密文。
    // 返回的 Length 是加密后精确字节数,供上传方设置 Content-Length 与服务端长度比对。
    public async Task<StreamUploadContent> PrepareFileUploadAsync(
        SendFileSource source,
        SymmetricCryptoKey cryptoKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cryptoKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.FileName);
        ArgumentNullException.ThrowIfNull(source.OpenReadAsync);
        SendFileSource.ValidateLength(source.Length);
        if (cryptoKey.MacKey is null)
            throw new CryptographicException("Send file encryption requires a MAC key.");

        var iv = RandomNumberGenerator.GetBytes(16);
        var mac = await ComputeFileMacAsync(source, cryptoKey, iv, ct).ConfigureAwait(false);
        // 1(header)+16(iv)+32(mac)+PKCS7 密文长度(明文长度向上取整到下一个 16 字节块)。
        var encryptedLength = checked(49L + ((source.Length / 16L) + 1L) * 16L);

        return new StreamUploadContent(
            encryptedLength,
            async (destination, writeCt) =>
            {
                await destination.WriteAsync(
                    new[] { (byte)EncryptionType.AesCbc256_HmacSha256_B64 }, writeCt).ConfigureAwait(false);
                await destination.WriteAsync(iv, writeCt).ConfigureAwait(false);
                await destination.WriteAsync(mac, writeCt).ConfigureAwait(false);
                await using var plaintext = await source.OpenReadAsync(writeCt).ConfigureAwait(false);
                using var aes = Aes.Create();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = cryptoKey.EncKey;
                aes.IV = iv;
                await using var encrypting = new CryptoStream(
                    destination, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
                await CopyExactlyAsync(plaintext, encrypting, source.Length, writeCt).ConfigureAwait(false);
                await encrypting.FlushFinalBlockAsync(writeCt).ConfigureAwait(false);
            });
    }

    // 第一遍:重开源,以固定 iv/密钥做 AES-CBC 加密,把 iv 与流式密文喂进 HMAC,得到 mac。绝不缓存密文。
    private static async Task<byte[]> ComputeFileMacAsync(
        SendFileSource source,
        SymmetricCryptoKey key,
        byte[] iv,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key.MacKey!);
        hash.AppendData(iv);
        await using var plaintext = await source.OpenReadAsync(ct).ConfigureAwait(false);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key.EncKey;
        aes.IV = iv;
        await using var sink = new IncrementalHashWriteStream(hash);
        await using var encrypting = new CryptoStream(
            sink, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        await CopyExactlyAsync(plaintext, encrypting, source.Length, ct).ConfigureAwait(false);
        await encrypting.FlushFinalBlockAsync(ct).ConfigureAwait(false);
        return hash.GetHashAndReset();
    }

    // 精确拷贝 expectedLength 字节:多读到(源在选择后变长)或读不满(变短)都抛 InvalidDataException,
    // 保证两遍读到的内容一致,且服务端长度比对不会失败。使用租借缓冲,归还前清零,常量内存。
    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long expectedLength,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > expectedLength)
                    throw new InvalidDataException("Send file grew after selection.");
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
            if (total != expectedLength)
                throw new InvalidDataException("Send file length changed after selection.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // 只写、把写入内容转发给 IncrementalHash 的适配流:让 CryptoStream 的密文直接进入 HMAC,无需中间缓冲。
    private sealed class IncrementalHashWriteStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            hash.AppendData(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // 密码证明 = base64( PBKDF2-SHA256(pw, salt=seed, iter=100000, 32B) )。发往服务端,非明文。
    public string ComputePasswordProof(string password, byte[] seed)
    {
        var proof = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            seed,
            SendKdfIterations,
            HashAlgorithmName.SHA256,
            32);
        return Convert.ToBase64String(proof);
    }

    // 分享 URL = serverUrl.TrimEnd('/') + "/#/send/" + accessId + "/" + base64urlNoPad(seed)。
    public string BuildShareUrl(string serverUrl, string accessId, byte[] seed)
        => $"{serverUrl.TrimEnd('/')}{ShareUrlMarker}{accessId}/{Base64UrlEncode(seed)}";

    // 解析分享 URL:在 "/#/send/" 处切分,后段按 '/' 取 accessId 与 base64url seed。
    public bool TryParseShareUrl(string url, out string accessId, out byte[] seed)
    {
        accessId = "";
        seed = Array.Empty<byte>();
        if (string.IsNullOrEmpty(url))
            return false;

        var idx = url.IndexOf(ShareUrlMarker, StringComparison.Ordinal);
        if (idx < 0)
            return false;

        var tail = url[(idx + ShareUrlMarker.Length)..];
        var slash = tail.IndexOf('/');
        if (slash <= 0 || slash == tail.Length - 1)
            return false;

        var id = tail[..slash];
        var seedSegment = tail[(slash + 1)..];
        try
        {
            seed = Base64UrlDecode(seedSegment);
        }
        catch (FormatException)
        {
            return false;
        }

        if (seed.Length == 0)
            return false;

        accessId = id;
        return true;
    }

    // base64url(无填充):'+'->'-','/'->'_',去掉 '='。
    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
