using System.Security.Cryptography;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.V2;
using Vault.Porting;
using Xunit;

namespace Vault.Tests;

public sealed class ProtectedVaultPortingServiceTests
{
    private const string Password = "independent-backup-password";

    [Theory]
    [InlineData(false, ExportFormat.PasswordProtectedJson)]
    [InlineData(true, ExportFormat.PasswordProtectedJson)]
    [InlineData(false, ExportFormat.AccountRestrictedJson)]
    [InlineData(true, ExportFormat.AccountRestrictedJson)]
    public async Task EncryptedExportUnlocksToACompletePersonalImportPreview(bool v2, ExportFormat format)
    {
        var session = NewSession(v2);
        try
        {
            var export = new VaultExportService(new VaultService(session), session);
            var import = new VaultImportService(null!, new CipherEncryptor(new CryptoService()), null!, session);
            var encrypted = await export.ExportProtectedAsync(format, Password, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("private-password-value", encrypted, StringComparison.Ordinal);
            Assert.DoesNotContain("Private work", encrypted, StringComparison.Ordinal);
            Assert.Equal(format == ExportFormat.PasswordProtectedJson ? ImportProtection.Password : ImportProtection.Account,
                import.InspectJsonProtection(encrypted));

            var plaintext = await import.UnlockJsonAsync(encrypted, Password, TestContext.Current.CancellationToken);
            var preview = import.Parse(ImportFormat.Json, plaintext);
            Assert.Null(preview.Error);
            Assert.Equal(1, preview.Ciphers);
            Assert.Equal(1, preview.Folders);
            var data = BitwardenJsonCodec.Parse(plaintext);
            Assert.Equal("private-password-value", Assert.Single(data.Ciphers).Login!.Password);
            Assert.Equal((0, 0), Assert.Single(data.Relations));
            Assert.DoesNotContain("Trash item", plaintext, StringComparison.Ordinal);
            Assert.DoesNotContain("Shared item", plaintext, StringComparison.Ordinal);
        }
        finally
        {
            session.Logout();
        }
    }

    [Fact]
    public async Task PasswordProtectedExportCanBeRestoredByAnotherUnlockedAccount()
    {
        var source = NewSession(v2: true);
        var destination = NewSession(v2: false);
        try
        {
            var encrypted = await new VaultExportService(new VaultService(source), source)
                .ExportProtectedAsync(ExportFormat.PasswordProtectedJson, Password, TestContext.Current.CancellationToken);
            var import = new VaultImportService(null!, new CipherEncryptor(new CryptoService()), null!, destination);
            var plaintext = await import.UnlockJsonAsync(encrypted, Password, TestContext.Current.CancellationToken);
            Assert.Equal("private-password-value", Assert.Single(BitwardenJsonCodec.Parse(plaintext).Ciphers).Login!.Password);
        }
        finally
        {
            source.Logout();
            destination.Logout();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountRestrictedExportRejectsAnotherAccountKey(bool v2)
    {
        var source = NewSession(v2);
        var destination = NewSession(v2);
        try
        {
            var encrypted = await new VaultExportService(new VaultService(source), source)
                .ExportProtectedAsync(ExportFormat.AccountRestrictedJson, null, TestContext.Current.CancellationToken);
            var import = new VaultImportService(null!, new CipherEncryptor(new CryptoService()), null!, destination);
            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                import.UnlockJsonAsync(encrypted, null, TestContext.Current.CancellationToken));
        }
        finally
        {
            source.Logout();
            destination.Logout();
        }
    }

    [Fact]
    public async Task CancelledProtectedOperationsDoNotReturnSensitivePayload()
    {
        var session = NewSession(v2: false);
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var export = new VaultExportService(new VaultService(session), session);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                export.ExportProtectedAsync(ExportFormat.PasswordProtectedJson, Password, cancellation.Token));
            var import = new VaultImportService(null!, new CipherEncryptor(new CryptoService()), null!, session);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                import.UnlockJsonAsync("{\"encrypted\":false,\"folders\":[],\"items\":[]}", null, cancellation.Token));
        }
        finally
        {
            session.Logout();
        }
    }

    [Theory]
    [InlineData(ExportFormat.PasswordProtectedJson)]
    [InlineData(ExportFormat.AccountRestrictedJson)]
    public async Task LockedAccountCannotExportProtectedBackup(ExportFormat format)
    {
        var session = NewSession(v2: true);
        session.Lock();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VaultExportService(new VaultService(session), session)
                .ExportProtectedAsync(format, Password, TestContext.Current.CancellationToken));
        session.Logout();
    }

    [Fact]
    public async Task ExportPolicyAlsoBlocksEncryptedFormats()
    {
        var session = NewSession(v2: false);
        try
        {
            session.SetSnapshot(new DecryptedVault(session.Account, [], [], 0, 0)
            {
                Organizations = [new VaultOrganization("org", "Team", 2, 2, true)],
                Policies = [new VaultOrganizationPolicy("policy", "org", (int)OrganizationPolicyType.DisablePersonalVaultExport, true, "null")],
            });
            var service = new VaultExportService(new VaultService(session), session);
            await Assert.ThrowsAsync<OrganizationPolicyException>(() =>
                service.ExportProtectedAsync(ExportFormat.PasswordProtectedJson, Password, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<OrganizationPolicyException>(() =>
                service.ExportProtectedAsync(ExportFormat.AccountRestrictedJson, null, TestContext.Current.CancellationToken));
        }
        finally
        {
            session.Logout();
        }
    }

    private static VaultSession NewSession(bool v2)
    {
        var session = new VaultSession();
        session.BeginAccountSession(Guid.NewGuid().ToString("D"), "owner@example.com", ServerEndpoints.ForSelfHosted("https://vault.example"));
        if (v2)
            session.SetUnlockedKey(V2UserKey.Generate());
        else
            session.SetUnlockedKey(new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64)));
        session.SetSnapshot(new DecryptedVault(
            session.Account,
            [new Folder { Id = "original-folder", Name = "Private work" }],
            [
                new Cipher
                {
                    Id = "original-item", Type = CipherType.Login, Name = "Private item", FolderId = "original-folder",
                    Login = new CipherLogin("user", "private-password-value", null, []),
                },
                new Cipher { Id = "trash", Type = CipherType.SecureNote, Name = "Trash item", DeletedDate = DateTimeOffset.UtcNow, SecureNote = new CipherSecureNote(0) },
                new Cipher { Id = "shared", Type = CipherType.Login, Name = "Shared item", OrganizationId = "org", Login = new CipherLogin("user", "shared-secret", null, []) },
            ],
            0,
            0));
        return session;
    }
}
