using App.Services;
using Core.Abstractions;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class UserVerificationServiceTests
{
    [Fact]
    public async Task HelloSuccess_DoesNotPromptPassword()
    {
        var hello = new Hello { Available = true, Signature = [1, 2, 3] };
        var account = new Verifier(true);
        var service = new UserVerificationService(hello, account);
        var prompts = 0;

        var verified = await service.VerifyAsync(_ =>
        {
            prompts++;
            return Task.FromResult<string?>("master");
        });

        Assert.True(verified);
        Assert.Equal(0, prompts);
        Assert.Equal(0, account.Calls);
    }

    [Fact]
    public async Task HelloUnavailableOrFailed_FallsBackToMasterPassword()
    {
        var service = new UserVerificationService(
            new Hello { Available = false },
            new Verifier(true));

        var verified = await service.VerifyAsync(
            _ => Task.FromResult<string?>("master"));

        Assert.True(verified);
    }

    [Fact]
    public async Task FailedFallback_ReturnsFalse()
    {
        var service = new UserVerificationService(
            new Hello { Available = true, Signature = null },
            new Verifier(false));

        Assert.False(await service.VerifyAsync(
            _ => Task.FromResult<string?>("wrong")));
    }

    private sealed class Hello : IWindowsHelloAuthenticator
    {
        public bool Available { get; init; }
        public byte[]? Signature { get; init; }
        public Task<bool> IsAvailableAsync() => Task.FromResult(Available);
        public Task<byte[]?> CreateProtectorAsync(byte[] challenge, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<byte[]?> GetProtectorAsync(byte[] challenge, CancellationToken ct = default) =>
            Task.FromResult(Signature);
    }

    private sealed class Verifier(bool result) : IAccountService
    {
        public int Calls { get; private set; }
        public Task<bool> VerifyMasterPasswordAsync(string password, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
        public Task UpdateNameAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangePasswordAsync(string currentPassword, string newPassword, string? hint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ChangeKdfAsync(string currentPassword, int newIterations, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
