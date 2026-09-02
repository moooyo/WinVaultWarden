using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using Crypto.V2;
using Xunit;

namespace Crypto.Tests;

public sealed class V2CryptoTests
{
    private const string FixedXaesUserKey =
        "pQEEAlAAAQIDBAUGBwgJCgsMDQ4PAzoAARF5BIIDBCBYIAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fAQ==";

    private const string HistoricalV2UserKey =
        "pQEEAlCxZkKFDpp70P5mWPmOjf3xAzoAARF5BIQDBAUGIFggCFcd6XLISUfLaITyU9yimrYHacdS5XhBayO2663jdSUB";

    private const string HistoricalWrappedPrivateKey =
        "7.g1gdowE6AAEReQMZARwEULFmQoUOmnvQ/mZY+Y6N/fGhBVgYe16rmgYXX3Orgo6y5U5Z8eb+JHTGfcivWQTR+1rVWtHJhEm8G/AtE78Ud3S8qxZmstUKhC5u9xgPvx2e8Fe8QL80Dv0WoEsy0XEb+5EFd8xDlu7OBuCVv2MaoJ/XzAkbpn9IT1vMCPhvRuaktIWMNrQgJ1jnmqjTGObftA02sHnj938tLRNfilw8ln/PBO2GBZQVzTUYfnc+mBeedGyZAxhSxyUwtFB8h3HC/t9BGtLT/bm83Df8rwTc+rGFL5r+T6vczQ+6hvF6kKpUb37XwgLEDsc+J4UTb+4zHaDcTioOYq6Hki8PrsN9PWL57nkhRMi3fKgfz8GDtY+pjp7D9HYV6OMuveSK9l+h16enJwiFDy6XEx+eth4aHPT5hybnOfTWbkEIhUmPD3K2JKvUUxeL9Z6e1EtSylVitO4Lit485KYaY8VASW4MnAzPOUQVwZ4jowHr5X8g0jVtHiLeUuOwDGcqjO/q6//tkiCwjW/W79jk4eqMtqPbOl0XelYVmM4KZCslPZ+2IYS56g/gl8Q2Oj9UGq7QJCsZvV9rBNa4wS3uC9atoWWRqO2PTWkVTurakkK3Fc9VP2bC1lJaWoWVjYpyJJVZh77ktpD3VrFdrT62+de0iaWUAtAr/1ALToNzoTYu3ihyGb6FZMN//XLTKk8GhZGVCluEDClHnziBxCX7Qg/0HRiU7EjsYGhpFnmG2XkvZQb9Pds8gucTbmbUeVfjXZ/IOLm16G/tdit2VIf80zcsvhgxTYys4Cm12N+62fM3aT5L9lqWvBYOMDksy00/3uLPzWbLFWbKItaC1c+bceGS7UDrLim6Pm/Voo2jXCi6EHpXX2/THrJybRDwqmQi7UVWXR3aPx//q9busEXxRyeu0m4lq2AjhQWhOvfPjpJzNX1hRE9Bu7UKYJhUF6DAsXFFKpob0LoARpcjGLFLcO61yV6He2nQFAa+ULXxhrKbISzqO3Q2xMs2p3jQ4Ctm0T+03w9Y5/Yf1qNKaL6AayA2nf0thYgh+OHNEnnkFwvBnTyB5B32E+/cUy7bb3329Pz7h+ruLo5IhGZM5GiEjF4vOSZmZJZ1t2eR4U7oxX0VTpwFPPBUQ3O7A5C2l0g/pGCFda4QlgR5qRA09kaAd9VBSJbQABGH0zWlXNPAjPQ6M9CxxTv9lM/72RSzTvnJqjQNpWGQjYuTi++EN5QZ37Nmlcw9eSa6X1C97ADndWV46dlFowUUDXiczi+Q0bZmFtpvkRg0TWlicS/cURLIfpG7sGwgqIis5R4haQ+RDB1+4oC0xmncWqy7vMESW6trh+icEL2PybwGPnzdngUqEIw5fG9huX3BmxbJjukSjWWk2CH8AaY2lHRXttzpOhpfP9c1cmrwXXUuHwTFMiKdmdwSqGbgebUP25kB9priXO88Jri3Wb739KRV5M2k6/9AspCwpOqlKN6MZm2vElNI+cXSWMHeX3666p4ALr7Vu7+q7iw4s4cO09MMJWsaiTaZBsVRhdoocsej+091JM/yJ29TVDJEMp2vEiia8HQ4k2bH9W9XCB71cpygRMYTFRDJ3Yjly4MYg7whBQnkeu8IYagCY6UZ60V73qhKRZJKuiV6ZTC+objnMPMmi9Kd05WmYFab8ZDP8s4yhU0WJNXdZGwpX7pnoi0T+g/y94sfZNGs5QuKgNEX";

    private const string HistoricalWrappedSigningKey =
        "7.g1gcowE6AAEReQMYZQRQsWZChQ6ae9D+Zlj5jo398aEFWBj8Gg/gn4tQKWO3nq5e/2p9gkzIrKD829RYT3aEUIDOetEtnFqRuQ3Cz13693WqDnKHM5Buzi6LcTsxo1jphYR7vlE5nYLjCpOCAftPN1oLfs5SCNkwwMENhujpVftfDzciE99aLEJDS9A=";

    private const string HistoricalVerifyingKey =
        "pgEBAlAmkP0QgfdMVbIujX55W/yNAycEgQIgBiFYIEM6JxBmjWQTruAm3s6BTaJy1q6BzQetMBacNeRJ0kxR";

    private const string HistoricalSignedPublicKey =
        "hFgepAEnAxg8BFAmkP0QgfdMVbIujX55W/yNOgABOH8BoFkBTqNpYWxnb3JpdGhtAG1jb250ZW50Rm9ybWF0AGlwdWJsaWNLZXlZASYwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQDP/7WM8nUepxoJ0qtM+azxcly+eZ31qUjjZTZcX/gYw1MzkoXWAjqyeFH/bdktq1lEUwegrxkIxKkY2SMtp0CvPnaV1x5O8E6FBSJbKWRlDg181rfEhgm5tc6aR4PJ827IvFVm9xk6Sj091P5DHZDEOsWLZc2jYjtpUV3X38I4gSR7HiYnR4DcwcWkoJ3FhtxMCwYgPz6RVH0vzhLUmm1mgbzH6IH8Pf9DjLTZSxBikVO7S9s9jzhiZbTeeAl3FbNLxfj9Qkj+NoSfms7jGVTlBwvSXgjJs/ktGkT1cR5QcBMpU4bt41+l73MN8pXapCih9Awf1W+RY7imxpYOMFJ3AgMBAAFYQMq/hT4wod2w8xyoM7D86ctuLNX4ZRo+jRHf2sZfaO7QsvonG/ZYuNKF5fq8wpxMRjfoMvnY2TTShbgzLrW8BA4=";

    private const string HistoricalSignedSecurityState =
        "hFgepAEnAxg8BFAmkP0QgfdMVbIujX55W/yNOgABOH8CoFgkomhlbnRpdHlJZFBHOOw2BI9OQoNq+Vl1xZZKZ3ZlcnNpb24CWEAlchbJR0vmRfShG8On7Q2gknjkw4Dd6MYBLiH4u+/CmfQdmjNZdf6kozgW/6NXyKVNu8dAsKsin+xxXkDyVZoG";

    private const string OfficialMldsa44SigningKey =
        "pgEHAlDhGjOTZpUxKHh8GywWtpRYAzgvBIEBIFkFIFzDYgpVGgITlyrYReSTD07N/h0QqB/96M3RoP/8OO76P9ZZAoytNFyCMHTocP+57RLh0IpAfbjyQx8rddOTToomYsAzyDN67Br9wbsbq/GF1wk2WjBXtBd03PCOOHfU/cohEbd47VP2z1stRtSiQn7nLBwIqH5OIxeU2EGFE7rl5X1lQoxB+gsQMda8OwehXzNJxDYaYnxzbU6G/jKF50J3EX9X30vFOpivzVWneu5+GxRlq7cuaKstqJfO7PuPfU8M7Q3POVBrMaRreVyM7j7m14mh+PfDXrIOsXxq9UApVOxutBV26y1lB4uXVayg468R9DjfKoq/s111s/0VEJlzWQj5sVpC9SEdTqaRAUFCra6cn98M9wS8QZfRD49fYL4pq8gFxvoPYZLk0SyPfVAong6nliF/RTcwylrxrD6zyMzEvBYZJYBlBhYEiaF1oHvPD5MjpNBQExF7pfPG1bxvb+rc/Qk+QO+8GbhkjMglHW0vrKXae1BuxLnma4IcQXYgW5zgzUw1i14qdCyTud20gf7KELshOSXhxmEUnAToSngJ5EDBkJd88BXxK2JZWBw2jHDuxjOYW3jBEI9/9C0csD2c6Vf3BbhxtXw3Ba2m1rOG5kejus+EbaT+tcUIvElgwtH1N4cAsRj9LqINsnkKr7fTkEO9Krb4kCERpF+xgb6Og/paTEPl0tVnlwQvjQw+gyhX2w/Mxh5+o2FrcNoEOe7fGhpAGWQ1yZsk4qsH+FFScpcvrq+yZw/2ayN7AL8JLaLemKme1DGUmLTYOFqmjjaXS5wx1Est/r2nizRluFkpbGOVjLy1h92C4ExRmG/bng86KUHvHqyNOwjC/NMs653ENAlsfwPlKcghMs1+JCpWaFILrwAzKi3opPUkIDeDUJk701xXO6TzLlrYndyht7PdsKIbD6o2dN4r3LNRm4btM6iTnmx8u5uUkyedsoc5sWORZ3a9zED3NoAAtc77NCW3fc6EqVdkIxnLlSJuMOEZpORVeVsk5ie2mIHchwhMPBly/RcMh7z0zCKL60CDoGPnpExPqEkPSEV9EuTPRLhO9BVW5TWZxbSgMLTQfuZZS2gwrAy3gj+QgTD9bS8/m11lAS+Omr7rdPQEKiqMBfyyj5aPFfQjDfzgNFlRQcdwwgDB/XEOhtm02CDuDwFXYlJstaFI4Yh0o4Ws72+e+3fUO8FJNuiqlm3VOh0kA6gg9zX9YPWGdXD6k/+uMhTcmsk7LBkRFaAEX3GSMkmhD92Kpso19kIPocgKhn1/VO9UtGXxUd4LmgQMMdc3PIFWXVA8wTMtxoCFMxcf8lE6cTna1jsYSLpG8JJV6B9+2rYgjmLReVWqQ+J8dLXtd7jTxm6lDEeHYODbXBuJcKIudKKb0HvtC6fpx6GGZTGIn6UW/9ZsNj2VWLJxbWeAnYXxa04Y9FbV7CND0I6mAdp3Z2JGwmQyv82TXA2AoeMvxBs4Df8EGiYpaDVA1fK52e9hARFCQlCMaJh6RKAWm/j72r8ZVL1U10Xx1mtzZ4SOQ+DJSm2IzL39ZTMzAdNNBtQA5kPjbGgcf0dbACaWfWjlivL79IAMp6rWpDo3grHZRSIm74gdUPy7e03wyMOxXKgDIcu5KPqNzkBI8B/EgSHqodoKlUphUMPS5CrdG09YkQwtsIaaVIHUae9L3M7D58CiElP9Gc6UGxaAi/FPym/sKxun93FYAjUDTlypV2NyHWrdmqORT0drNwl85nsADV0hWCDWs2FYh/qBnZshVlSiSUxxSmTUsMmZcUnuhTwrwKQV8w==";

    private const string OfficialMldsa44VerifyingKey =
        "pQEHAlDhGjOTZpUxKHh8GywWtpRYAzgvBIECIFkFIFzDYgpVGgITlyrYReSTD07N/h0QqB/96M3RoP/8OO76P9ZZAoytNFyCMHTocP+57RLh0IpAfbjyQx8rddOTToomYsAzyDN67Br9wbsbq/GF1wk2WjBXtBd03PCOOHfU/cohEbd47VP2z1stRtSiQn7nLBwIqH5OIxeU2EGFE7rl5X1lQoxB+gsQMda8OwehXzNJxDYaYnxzbU6G/jKF50J3EX9X30vFOpivzVWneu5+GxRlq7cuaKstqJfO7PuPfU8M7Q3POVBrMaRreVyM7j7m14mh+PfDXrIOsXxq9UApVOxutBV26y1lB4uXVayg468R9DjfKoq/s111s/0VEJlzWQj5sVpC9SEdTqaRAUFCra6cn98M9wS8QZfRD49fYL4pq8gFxvoPYZLk0SyPfVAong6nliF/RTcwylrxrD6zyMzEvBYZJYBlBhYEiaF1oHvPD5MjpNBQExF7pfPG1bxvb+rc/Qk+QO+8GbhkjMglHW0vrKXae1BuxLnma4IcQXYgW5zgzUw1i14qdCyTud20gf7KELshOSXhxmEUnAToSngJ5EDBkJd88BXxK2JZWBw2jHDuxjOYW3jBEI9/9C0csD2c6Vf3BbhxtXw3Ba2m1rOG5kejus+EbaT+tcUIvElgwtH1N4cAsRj9LqINsnkKr7fTkEO9Krb4kCERpF+xgb6Og/paTEPl0tVnlwQvjQw+gyhX2w/Mxh5+o2FrcNoEOe7fGhpAGWQ1yZsk4qsH+FFScpcvrq+yZw/2ayN7AL8JLaLemKme1DGUmLTYOFqmjjaXS5wx1Est/r2nizRluFkpbGOVjLy1h92C4ExRmG/bng86KUHvHqyNOwjC/NMs653ENAlsfwPlKcghMs1+JCpWaFILrwAzKi3opPUkIDeDUJk701xXO6TzLlrYndyht7PdsKIbD6o2dN4r3LNRm4btM6iTnmx8u5uUkyedsoc5sWORZ3a9zED3NoAAtc77NCW3fc6EqVdkIxnLlSJuMOEZpORVeVsk5ie2mIHchwhMPBly/RcMh7z0zCKL60CDoGPnpExPqEkPSEV9EuTPRLhO9BVW5TWZxbSgMLTQfuZZS2gwrAy3gj+QgTD9bS8/m11lAS+Omr7rdPQEKiqMBfyyj5aPFfQjDfzgNFlRQcdwwgDB/XEOhtm02CDuDwFXYlJstaFI4Yh0o4Ws72+e+3fUO8FJNuiqlm3VOh0kA6gg9zX9YPWGdXD6k/+uMhTcmsk7LBkRFaAEX3GSMkmhD92Kpso19kIPocgKhn1/VO9UtGXxUd4LmgQMMdc3PIFWXVA8wTMtxoCFMxcf8lE6cTna1jsYSLpG8JJV6B9+2rYgjmLReVWqQ+J8dLXtd7jTxm6lDEeHYODbXBuJcKIudKKb0HvtC6fpx6GGZTGIn6UW/9ZsNj2VWLJxbWeAnYXxa04Y9FbV7CND0I6mAdp3Z2JGwmQyv82TXA2AoeMvxBs4Df8EGiYpaDVA1fK52e9hARFCQlCMaJh6RKAWm/j72r8ZVL1U10Xx1mtzZ4SOQ+DJSm2IzL39ZTMzAdNNBtQA5kPjbGgcf0dbACaWfWjlivL79IAMp6rWpDo3grHZRSIm74gdUPy7e03wyMOxXKgDIcu5KPqNzkBI8B/EgSHqodoKlUphUMPS5CrdG09YkQwtsIaaVIHUae9L3M7D58CiElP9Gc6UGxaAi/FPym/sKxun93FYAjUDTlypV2NyHWrdmqORT0drNwl85nsADV0=";

    private const string OfficialMldsa44Signature =
        "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";

    [Theory]
    [InlineData(
        1,
        "",
        "ce546ef63c9cc60765923609b33a9a1974e96e52daf2fcf7075e2271")]
    [InlineData(
        3,
        "c2sp.org/XAES-256-GCM",
        "986ec1832593df5443a179437fd083bf3fdb41abd740a21f71eb769d")]
    public void Xaes256Gcm_MatchesC2spVectors(
        byte keyByte,
        string associatedData,
        string expectedCiphertext)
    {
        var key = Enumerable.Repeat(keyByte, Xaes256Gcm.KeySize).ToArray();
        var nonce = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWX");
        var plaintext = Encoding.ASCII.GetBytes("XAES-256-GCM");
        var aad = Encoding.ASCII.GetBytes(associatedData);

        var encrypted = Xaes256Gcm.Encrypt(key, nonce, plaintext, aad);
        Assert.Equal(expectedCiphertext, Convert.ToHexString(encrypted).ToLowerInvariant());
        Assert.Equal(plaintext, Xaes256Gcm.Decrypt(key, nonce, encrypted, aad));
    }

    [Fact]
    public void V2UserKey_ImportsAndReexportsOfficialCoseKeyVector()
    {
        using var key = V2UserKey.ImportBase64(FixedXaesUserKey);

        Assert.Equal("000102030405060708090a0b0c0d0e0f", key.KeyIdHex);
        Assert.Equal(FixedXaesUserKey, key.ExportBase64());
    }

    [Fact]
    public void V2UserKey_RejectsInvalidPaddingAndTrailingCbor()
    {
        var invalidPadding = Convert.FromBase64String(FixedXaesUserKey);
        invalidPadding[^1] = 2;
        Assert.Throws<CryptographicException>(() => V2UserKey.ImportTransport(invalidPadding));

        var valid = Convert.FromBase64String(FixedXaesUserKey);
        var trailing = new byte[valid.Length + 1];
        valid.CopyTo(trailing, 0);
        trailing[^1] = 1;
        Assert.Throws<FormatException>(() => V2UserKey.ImportTransport(trailing));
    }

    [Fact]
    public void HistoricalWrappedSigningKey_DecryptsAndDerivesOfficialVerifyingKey()
    {
        using var userKey = V2UserKey.ImportBase64(HistoricalV2UserKey);
        var signingKeyBytes = userKey.Decrypt(
            HistoricalWrappedSigningKey,
            CoseContentFormat.CoseKey);

        try
        {
            using var signingKey = AccountSigningKey.ImportSigningKeyCose(signingKeyBytes);
            Assert.Equal(AccountSignatureAlgorithm.Ed25519, signingKey.Algorithm);
            Assert.Equal(HistoricalVerifyingKey, signingKey.ExportVerifyingKeyCoseBase64());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKeyBytes);
        }
    }

    [Fact]
    public void HistoricalSignedSecurityState_VerifiesWithOfficialEd25519Key()
    {
        var payload = CoseSign1Codec.VerifyAndExtract(
            HistoricalSignedSecurityState,
            HistoricalVerifyingKey,
            BitwardenSigningNamespace.SecurityState);
        try
        {
            var reader = new CborReader(payload, CborConformanceMode.Lax);
            Assert.Equal(2, reader.ReadStartMap());
            Assert.Equal("entityId", reader.ReadTextString());
            Assert.Equal(16, reader.ReadByteString().Length);
            Assert.Equal("version", reader.ReadTextString());
            Assert.Equal(2UL, reader.ReadUInt64());
            reader.ReadEndMap();
            Assert.Equal(0, reader.BytesRemaining);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [Fact]
    public void HistoricalV2AccountGraph_UnlocksAndAuthenticatesOfficialVector()
    {
        using var userKey = V2UserKey.ImportBase64(HistoricalV2UserKey);
        var state = new V2AccountCryptographicState(
            HistoricalWrappedPrivateKey,
            HistoricalSignedPublicKey,
            HistoricalWrappedSigningKey,
            HistoricalVerifyingKey,
            HistoricalSignedSecurityState,
            2);

        using var unlocked = V2AccountCryptographicStateCodec.UnlockAndVerify(userKey, state);
        Assert.Equal(AccountSignatureAlgorithm.Ed25519, unlocked.SigningKey.Algorithm);
        Assert.Equal(HistoricalVerifyingKey, unlocked.SigningKey.ExportVerifyingKeyCoseBase64());
    }

    [Fact]
    public void Mldsa44_ImportsAndVerifiesOfficialRustSdkVector()
    {
        var signingKeyBytes = Convert.FromBase64String(OfficialMldsa44SigningKey);
        var verifyingKeyBytes = Convert.FromBase64String(OfficialMldsa44VerifyingKey);
        var signature = Convert.FromBase64String(OfficialMldsa44Signature);
        try
        {
            using var signingKey = AccountSigningKey.ImportSigningKeyCose(signingKeyBytes);
            Assert.Equal(AccountSignatureAlgorithm.Mldsa44, signingKey.Algorithm);
            Assert.Equal(OfficialMldsa44SigningKey, signingKey.ExportSigningKeyCoseBase64());
            Assert.Equal(OfficialMldsa44VerifyingKey, signingKey.ExportVerifyingKeyCoseBase64());

            using var verifyingKey = AccountVerifyingKey.Import(verifyingKeyBytes);
            Assert.True(verifyingKey.Verify("Test message"u8, signature));

            using var managedSigningKey = AccountSigningKey.ImportSigningKeyCoseManaged(signingKeyBytes);
            Assert.Equal(OfficialMldsa44SigningKey, managedSigningKey.ExportSigningKeyCoseBase64());
            Assert.Equal(OfficialMldsa44VerifyingKey, managedSigningKey.ExportVerifyingKeyCoseBase64());
            using var managedVerifyingKey = AccountVerifyingKey.ImportManaged(verifyingKeyBytes);
            Assert.True(managedVerifyingKey.Verify("Test message"u8, signature));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKeyBytes);
            CryptographicOperations.ZeroMemory(verifyingKeyBytes);
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    [Fact]
    public void ManagedMldsa44_GeneratesSignsAndCrossVerifies()
    {
        using var signingKey = AccountSigningKey.GenerateManagedMldsa44();
        var signingKeyCose = signingKey.ExportSigningKeyCose();
        var verifyingKeyCose = signingKey.ExportVerifyingKeyCose();
        var payload = "managed ML-DSA-44 fallback"u8.ToArray();
        byte[]? signature = null;
        try
        {
            using var importedSigningKey =
                AccountSigningKey.ImportSigningKeyCoseManaged(signingKeyCose);
            Assert.Equal(
                signingKey.ExportVerifyingKeyCoseBase64(),
                importedSigningKey.ExportVerifyingKeyCoseBase64());

            signature = importedSigningKey.Sign(payload);
            using var managedVerifier = AccountVerifyingKey.ImportManaged(verifyingKeyCose);
            Assert.True(managedVerifier.Verify(payload, signature));

            using var preferredVerifier = AccountVerifyingKey.Import(verifyingKeyCose);
            Assert.True(preferredVerifier.Verify(payload, signature));

            var signedObject = CoseSign1Codec.SignToBase64(
                payload,
                importedSigningKey,
                BitwardenSigningNamespace.SecurityState);
            Assert.Equal(
                payload,
                CoseSign1Codec.VerifyAndExtract(
                    signedObject,
                    importedSigningKey.ExportVerifyingKeyCoseBase64(),
                    BitwardenSigningNamespace.SecurityState));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKeyCose);
            CryptographicOperations.ZeroMemory(verifyingKeyCose);
            CryptographicOperations.ZeroMemory(payload);
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
        }
    }

    [Theory]
    [InlineData(CoseContentFormat.Utf8)]
    [InlineData(CoseContentFormat.Pkcs8PrivateKey)]
    [InlineData(CoseContentFormat.SpkiPublicKey)]
    [InlineData(CoseContentFormat.CoseKey)]
    [InlineData(CoseContentFormat.BitwardenLegacyKey)]
    [InlineData(CoseContentFormat.OctetStream)]
    [InlineData(CoseContentFormat.Cbor)]
    public void Type7_RoundTripsEverySupportedContentFormat(CoseContentFormat contentFormat)
    {
        using var key = V2UserKey.Generate();
        var plaintext = Encoding.UTF8.GetBytes("Bitwarden V2 content");

        var encrypted = key.Encrypt(plaintext, contentFormat);
        var parsed = CoseEncrypt0Codec.ParseEncString(encrypted);
        var decrypted = key.Decrypt(encrypted, contentFormat);

        Assert.Equal(contentFormat, parsed.ContentFormat);
        Assert.Equal(key.KeyIdHex, parsed.KeyIdHex);
        Assert.Equal(Xaes256Gcm.NonceSize, parsed.Nonce.Length);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Type7_RejectsWrongKeyIdBeforeDecrypting()
    {
        using var key = V2UserKey.Generate();
        using var other = V2UserKey.Generate();
        var encrypted = key.Encrypt("secret"u8, CoseContentFormat.OctetStream);

        Assert.Throws<CryptographicException>(
            () => other.Decrypt(encrypted, CoseContentFormat.OctetStream));
    }

    [Fact]
    public void Type7_IsUntaggedAndRejectsWrongContentTypeAndTrailingCbor()
    {
        using var key = V2UserKey.Generate();
        var encrypted = key.Encrypt("secret"u8, CoseContentFormat.OctetStream);
        var encoded = Convert.FromBase64String(encrypted[2..]);

        Assert.Equal(0x83, encoded[0]);
        Assert.Throws<CryptographicException>(() =>
            key.Decrypt(encrypted, CoseContentFormat.Cbor));

        var trailing = new byte[encoded.Length + 1];
        encoded.CopyTo(trailing, 0);
        trailing[^1] = 0;
        Assert.Throws<FormatException>(() =>
            CoseEncrypt0Codec.ParseEncString($"7.{Convert.ToBase64String(trailing)}"));
    }

    [Fact]
    public void CoseSign1_RejectsTamperingNamespaceAndWrongKeyId()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var signingKey = AccountSigningKey.GenerateMldsa44();
        using var otherKey = AccountSigningKey.GenerateMldsa44();
        var payload = "signed payload"u8.ToArray();
        var signed = CoseSign1Codec.SignToBase64(
            payload,
            signingKey,
            BitwardenSigningNamespace.SecurityState);

        Assert.Equal(
            payload,
            CoseSign1Codec.VerifyAndExtract(
                signed,
                signingKey.ExportVerifyingKeyCoseBase64(),
                BitwardenSigningNamespace.SecurityState));
        Assert.Throws<CryptographicException>(() =>
            CoseSign1Codec.VerifyAndExtract(
                signed,
                signingKey.ExportVerifyingKeyCoseBase64(),
                BitwardenSigningNamespace.SignedPublicKey));
        Assert.Throws<CryptographicException>(() =>
            CoseSign1Codec.VerifyAndExtract(
                signed,
                otherKey.ExportVerifyingKeyCoseBase64(),
                BitwardenSigningNamespace.SecurityState));

        var tamperedBytes = Convert.FromBase64String(signed);
        tamperedBytes[^1] ^= 1;
        var tampered = Convert.ToBase64String(tamperedBytes);
        Assert.Throws<CryptographicException>(() =>
            CoseSign1Codec.VerifyAndExtract(
                tampered,
                signingKey.ExportVerifyingKeyCoseBase64(),
                BitwardenSigningNamespace.SecurityState));
    }

    [Fact]
    public void GeneratedAccountMaterial_UnlocksAndVerifiesItsOwnState()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var material = V2AccountKeyMaterial.Generate();
        var state = new V2AccountCryptographicState(
            material.WrappedPrivateKey,
            material.SignedPublicKey,
            material.WrappedSigningKey,
            material.VerifyingKey,
            material.SecurityState,
            material.SecurityVersion);

        using var unlocked = V2AccountCryptographicStateCodec.UnlockAndVerify(material.UserKey, state);
        var privateKey = unlocked.ExportPrivateKeyPkcs8();
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKey, out var bytesRead);
            Assert.Equal(privateKey.Length, bytesRead);
            Assert.Equal(2048, rsa.KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    [Fact]
    public void AccountState_DerivesVerifyingKeyWhenWireValueIsAbsent()
    {
        using var material = V2AccountKeyMaterial.Generate();
        var state = new V2AccountCryptographicState(
            material.WrappedPrivateKey,
            material.SignedPublicKey,
            material.WrappedSigningKey,
            null,
            material.SecurityState,
            material.SecurityVersion);

        using var unlocked = V2AccountCryptographicStateCodec.UnlockAndVerify(material.UserKey, state);
        Assert.Equal(AccountSignatureAlgorithm.Mldsa44, unlocked.SigningKey.Algorithm);
        Assert.Equal(material.VerifyingKey, unlocked.SigningKey.ExportVerifyingKeyCoseBase64());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AccountMaterialGeneration_ZeroesEveryExportedBufferOnFailure(int failingStage)
    {
        var observed = new List<byte[]>();
        Assert.Throws<InjectedExportException>(() =>
            V2AccountKeyMaterial.GenerateWithExportObserverForTest((stage, buffer) =>
            {
                observed.Add(buffer);
                if (stage == failingStage)
                    throw new InjectedExportException();
            }));

        Assert.Equal(failingStage, observed.Count);
        foreach (var buffer in observed)
            Assert.All(buffer, value => Assert.Equal(0, value));
    }

    [Fact]
    public void AccountState_RejectsTamperedSigningKeyAfterPrivateKeyDecrypts()
    {
        using var material = V2AccountKeyMaterial.Generate();
        var encodedSigningKey = Convert.FromBase64String(material.WrappedSigningKey[2..]);
        encodedSigningKey[^1] ^= 1;
        var state = new V2AccountCryptographicState(
            material.WrappedPrivateKey,
            material.SignedPublicKey,
            $"7.{Convert.ToBase64String(encodedSigningKey)}",
            material.VerifyingKey,
            material.SecurityState,
            material.SecurityVersion);
        try
        {
            Assert.ThrowsAny<CryptographicException>(() =>
                V2AccountCryptographicStateCodec.UnlockAndVerify(material.UserKey, state));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedSigningKey);
        }
    }

    [Fact]
    public void TdeRegistrationMaterial_BuildsTheOfficialV2KeyGraph()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var organizationKey = RSA.Create(2048);
        var organizationSpki = organizationKey.ExportSubjectPublicKeyInfo();
        using var material = V2TdeRegistrationMaterial.Create(organizationSpki);
        var deviceMaterial = Assert.IsType<V2TrustedDeviceKeyMaterial>(material.TrustedDeviceKeys);
        var deviceKey = deviceMaterial.ExportDeviceKey();
        byte[]? resetUserKey = null;
        byte[]? devicePrivateKey = null;
        byte[]? deviceUserKey = null;
        byte[]? devicePublicKey = null;

        try
        {
            resetUserKey = DecryptRsaType4(material.ResetPasswordKey, organizationKey);
            using var resetImported = V2UserKey.ImportTransport(resetUserKey);
            Assert.Equal(material.AccountKeys.UserKey.ExportBase64(), resetImported.ExportBase64());

            devicePrivateKey = DecryptLegacyType2(deviceMaterial.EncryptedPrivateKey, deviceKey);
            using var deviceRsa = RSA.Create();
            deviceRsa.ImportPkcs8PrivateKey(devicePrivateKey, out var bytesRead);
            Assert.Equal(devicePrivateKey.Length, bytesRead);

            deviceUserKey = DecryptRsaType4(deviceMaterial.EncryptedUserKey, deviceRsa);
            using var deviceImported = V2UserKey.ImportTransport(deviceUserKey);
            Assert.Equal(material.AccountKeys.UserKey.ExportBase64(), deviceImported.ExportBase64());

            devicePublicKey = material.AccountKeys.UserKey.Decrypt(
                deviceMaterial.EncryptedPublicKey,
                CoseContentFormat.SpkiPublicKey);
            Assert.Equal(deviceRsa.ExportSubjectPublicKeyInfo(), devicePublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(organizationSpki);
            CryptographicOperations.ZeroMemory(deviceKey);
            if (resetUserKey is not null)
                CryptographicOperations.ZeroMemory(resetUserKey);
            if (devicePrivateKey is not null)
                CryptographicOperations.ZeroMemory(devicePrivateKey);
            if (deviceUserKey is not null)
                CryptographicOperations.ZeroMemory(deviceUserKey);
            if (devicePublicKey is not null)
                CryptographicOperations.ZeroMemory(devicePublicKey);
        }
    }

    [Fact]
    public void TdeRegistrationMaterial_CanSkipDeviceCompanionMaterial()
    {
        if (!AccountSigningKey.IsMldsa44Supported)
            return;

        using var organizationKey = RSA.Create(2048);
        var organizationSpki = organizationKey.ExportSubjectPublicKeyInfo();
        try
        {
            using var material = V2TdeRegistrationMaterial.Create(
                organizationSpki,
                trustDevice: false);
            Assert.Null(material.TrustedDeviceKeys);
            Assert.StartsWith("4.", material.ResetPasswordKey, StringComparison.Ordinal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(organizationSpki);
        }
    }

    private static byte[] DecryptRsaType4(string encString, RSA key)
    {
        Assert.StartsWith("4.", encString, StringComparison.Ordinal);
        var ciphertext = Convert.FromBase64String(encString[2..]);
        try
        {
            return key.Decrypt(ciphertext, RSAEncryptionPadding.OaepSHA1);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static byte[] DecryptLegacyType2(string encString, ReadOnlySpan<byte> key)
    {
        var parts = encString.Split('|', StringSplitOptions.None);
        Assert.Equal(3, parts.Length);
        Assert.StartsWith("2.", parts[0], StringComparison.Ordinal);
        var iv = Convert.FromBase64String(parts[0][2..]);
        var ciphertext = Convert.FromBase64String(parts[1]);
        var suppliedMac = Convert.FromBase64String(parts[2]);
        var macInput = new byte[iv.Length + ciphertext.Length];
        iv.CopyTo(macInput, 0);
        ciphertext.CopyTo(macInput, iv.Length);
        var encryptionKey = key[..32].ToArray();
        var macKey = key[32..].ToArray();
        try
        {
            using var hmac = new HMACSHA256(macKey);
            var expectedMac = hmac.ComputeHash(macInput);
            try
            {
                Assert.True(CryptographicOperations.FixedTimeEquals(expectedMac, suppliedMac));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedMac);
            }

            using var aes = Aes.Create();
            aes.Key = encryptionKey;
            return aes.DecryptCbc(ciphertext, iv, PaddingMode.PKCS7);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(suppliedMac);
            CryptographicOperations.ZeroMemory(macInput);
            CryptographicOperations.ZeroMemory(encryptionKey);
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private sealed class InjectedExportException : Exception
    {
    }
}
