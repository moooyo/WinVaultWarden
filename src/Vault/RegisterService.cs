using System.Security.Cryptography;
using System.Net;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;

namespace Vault;

public sealed class RegisterService : IRegisterService
{
    private const int Iterations = 600_000;
    private const int MaxEmailLength = 256;
    private const int MaxNameLength = 50;
    private readonly CryptoService _crypto;
    private readonly IVaultApiClientProvider _clients;

    public RegisterService(CryptoService crypto, IVaultApiClientProvider clients)
    {
        _crypto = crypto;
        _clients = clients;
    }

    public async Task<RegistrationOutcome> RegisterAsync(
        string serverUrl,
        string email,
        string? name,
        string password,
        string? hint,
        CancellationToken ct = default)
    {
        if (!TryParseWebVault(serverUrl, out var webVault))
            throw new RegistrationException("请输入有效的服务器地址。");
        if (string.IsNullOrWhiteSpace(email)
            || email.Trim().Length > MaxEmailLength
            || !email.Contains('@'))
        {
            throw new RegistrationException("请输入有效的邮箱地址。");
        }
        if (string.IsNullOrEmpty(password))
            throw new RegistrationException("密码不能为空。");
        if (name is not null && name.Trim().Length > MaxNameLength)
            throw new RegistrationException($"昵称不能超过 {MaxNameLength} 个字符。");

        var endpoints = ServerEndpoints.FromWebVault(webVault);
        var normalizedEmail = MasterPasswordSaltPolicy.Resolve(email, explicitSalt: null);
        var normalizedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var api = _clients.GetAnonymous<IRegistrationApiClient>(endpoints);
        var useRegistrationFinish = true;
        RegistrationVerificationResult? verification = null;
        try
        {
            verification = await api.RequestRegistrationVerificationTokenAsync(
                new RegisterVerificationRequest(normalizedEmail, normalizedName),
                ct);
        }
        catch (VaultWriteException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            useRegistrationFinish = false;
        }
        catch (VaultWriteException ex)
        {
            throw new RegistrationException(ex.Message);
        }
        catch (InvalidDataException)
        {
            throw new RegistrationException("注册服务器返回了无效的验证响应。");
        }
        catch (Exception ex) when (IsVerificationTransportFailure(ex, ct))
        {
            throw new RegistrationException("无法连接注册服务器，请稍后重试。", ex);
        }

        if (useRegistrationFinish
            && verification is RegistrationVerificationResult.EmailVerificationSent)
        {
            return new RegistrationOutcome.EmailVerificationRequired();
        }

        var verificationToken = verification switch
        {
            RegistrationVerificationResult.InlineToken token
                when !string.IsNullOrWhiteSpace(token.Token) => token.Token,
            null when !useRegistrationFinish => null,
            _ => throw new RegistrationException("注册服务器返回了无效的验证响应。"),
        };

        var masterKey = _crypto.DeriveMasterKey(
            password,
            normalizedEmail,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null);
        var userKeyBytes = RandomNumberGenerator.GetBytes(64);
        // 提前构造:64 字节构造下 EncKey/MacKey 是 userKeyBytes 的独立拷贝,
        // 故须 Dispose 整个 userKey(清 FullKey+EncKey+MacKey 三份),仅清 userKeyBytes 会漏掉两份拷贝。
        var userKey = new SymmetricCryptoKey(userKeyBytes);
        try
        {
            var passwordHash = _crypto.ComputeMasterPasswordHash(masterKey, password);

            // Key = ProtectUserKey: stretched master key wraps UserKey
            var key = _crypto.ProtectUserKey(masterKey, userKey).ToString();

            using var rsa = RSA.Create(2048);
            var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

            // EncryptedPrivateKey = Encrypt(PKCS8 private key bytes, RAW UserKey)
            // 导出的 PKCS8 明文私钥用完(已拷进密文)立即清零,避免临时数组驻留内存。
            var pkcs8 = rsa.ExportPkcs8PrivateKey();
            string encryptedPrivateKey;
            try
            {
                encryptedPrivateKey = _crypto.Encrypt(pkcs8, userKey).ToString();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }

            var req = new RegisterRequest(
                normalizedEmail,
                normalizedName,
                passwordHash,
                string.IsNullOrEmpty(hint) ? null : hint,
                key,
                0,
                Iterations,
                null,
                null,
                new RegisterKeys(encryptedPrivateKey, publicKey));

            ct.ThrowIfCancellationRequested();
            try
            {
                RegistrationWriteResult writeResult;
                if (useRegistrationFinish)
                {
                    writeResult = await api.FinishRegistrationAsync(
                        new RegisterFinishRequest(
                            req.Email,
                            verificationToken!,
                            req.MasterPasswordHash,
                            req.MasterPasswordHint,
                            req.Key,
                            req.Kdf,
                            req.KdfIterations,
                            req.KdfMemory,
                            req.KdfParallelism,
                            req.Keys),
                        ct);
                }
                else
                {
                    writeResult = await api.RegisterAsync(req, ct);
                }

                if (writeResult is RegistrationWriteResult.OutcomeUncertain)
                    return new RegistrationOutcome.OutcomeUncertain();
                if (writeResult is not RegistrationWriteResult.Completed)
                    throw new RegistrationException(
                        "注册服务器返回了无法识别的完成状态。");
            }
            catch (VaultWriteException ex) when (IsUncertainStatus(ex.StatusCode))
            {
                return new RegistrationOutcome.OutcomeUncertain();
            }
            catch (VaultWriteException ex)
            {
                throw new RegistrationException(ex.Message);
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex, ct))
            {
                return new RegistrationOutcome.OutcomeUncertain();
            }
            catch (OperationCanceledException)
            {
                return new RegistrationOutcome.OutcomeUncertain();
            }
            catch (HttpRequestException)
            {
                return new RegistrationOutcome.OutcomeUncertain();
            }
            catch (IOException)
            {
                return new RegistrationOutcome.OutcomeUncertain();
            }

            return new RegistrationOutcome.Registered();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            // 清零 UserKey 全部支撑数组(含 FullKey==userKeyBytes 及 EncKey/MacKey 拷贝)。
            userKey.Dispose();
        }
    }

    private static bool TryParseWebVault(
        string? serverUrl,
        out ServerEndpoint endpoint)
    {
        try
        {
            return ServerEndpoint.TryParse(serverUrl, out endpoint);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            endpoint = default;
            return false;
        }
    }

    private static bool IsVerificationTransportFailure(
        Exception exception,
        CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested
        && !RequestTimeoutClassifier.IsTimeout(exception, callerToken)
        && exception is HttpRequestException or IOException or OperationCanceledException;

    private static bool IsUncertainStatus(HttpStatusCode? statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout
        || statusCode is not null && (int)statusCode.Value >= 500;
}
