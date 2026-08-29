using Core.Session;
using Vault;

namespace App.Tests;

internal static class TestSessionLeases
{
    public static VaultSessionLease CreateSessionLease()
    {
        var session = new VaultSession();
        return session.BeginAccountSession(
            "test-account",
            "test@example.com",
            new Core.Models.ServerOrigin("https://vault.example"));
    }
}
