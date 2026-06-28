using System.Security.Cryptography;
using System.Text;
using Core.Enums;

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
