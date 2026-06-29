using Api;
using Api.Dtos;

namespace Vault.Tests;

public sealed class FakeAccountApiClient : IAccountApiClient
{
    public ProfileUpdateRequest? Profile;
    public ChangePasswordRequest? Password;
    public ChangeKdfRequest? Kdf;
    public Exception? Throw;

    public void SetBaseAddress(string b) { }

    public Task UpdateProfileAsync(ProfileUpdateRequest r, CancellationToken ct = default)
    {
        if (Throw != null) throw Throw;
        Profile = r;
        return Task.CompletedTask;
    }

    public Task ChangePasswordAsync(ChangePasswordRequest r, CancellationToken ct = default)
    {
        if (Throw != null) throw Throw;
        Password = r;
        return Task.CompletedTask;
    }

    public Task ChangeKdfAsync(ChangeKdfRequest r, CancellationToken ct = default)
    {
        if (Throw != null) throw Throw;
        Kdf = r;
        return Task.CompletedTask;
    }
}
