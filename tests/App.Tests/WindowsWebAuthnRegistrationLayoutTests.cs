using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using App.Services;
using Core.Models;
using Xunit;

namespace App.Tests;

public sealed class WindowsWebAuthnRegistrationLayoutTests
{
    [Fact]
    public void StableRegistrationPrefixesMatchWindowsSdkLayoutOn64Bit()
    {
        if (IntPtr.Size != 8)
            return;

        var service = typeof(WindowsWebAuthnTwoFactorService);
        var relyingParty = Nested(service, "WebAuthnRpEntityInformation");
        var user = Nested(service, "WebAuthnUserEntityInformation");
        var coseParameter = Nested(service, "WebAuthnCoseCredentialParameter");
        var credentialEx = Nested(service, "WebAuthnCredentialEx");
        var options = Nested(service, "WebAuthnMakeCredentialOptionsV3");
        var attestation = Nested(service, "WebAuthnCredentialAttestationV3");

        Assert.Equal(32, Marshal.SizeOf(relyingParty));
        Assert.Equal(40, Marshal.SizeOf(user));
        Assert.Equal(24, Marshal.SizeOf(coseParameter));
        Assert.Equal(32, Marshal.SizeOf(credentialEx));
        Assert.Equal(80, Marshal.SizeOf(options));
        Assert.Equal(120, Marshal.SizeOf(attestation));
        Assert.Equal(64, Marshal.OffsetOf(options, "CancellationId").ToInt32());
        Assert.Equal(72, Marshal.OffsetOf(options, "ExcludeCredentialList").ToInt32());
        Assert.Equal(72, Marshal.OffsetOf(attestation, "AttestationObject").ToInt32());
        Assert.Equal(88, Marshal.OffsetOf(attestation, "CredentialId").ToInt32());
        Assert.Equal(112, Marshal.OffsetOf(attestation, "UsedTransport").ToInt32());
    }

    [Fact]
    public void RegistrationClientDataUsesCreateTypeAndBoundOrigin()
    {
        var challenge = new WebAuthnRegistrationChallenge(
            "AQIDBAUGBwgJCgsMDQ4PEA",
            "https://vault.example.com",
            "vault.example.com",
            "Vault",
            [1, 2, 3, 4],
            "person@example.com",
            "Person",
            [new WebAuthnPublicKeyCredentialParameter("public-key", -7)],
            [],
            60_000,
            null,
            "discouraged",
            false,
            "discouraged",
            "none",
            null);
        var method = typeof(WindowsWebAuthnTwoFactorService).GetMethod(
            "BuildRegistrationClientDataJson",
            BindingFlags.NonPublic | BindingFlags.Static);

        var json = Assert.IsType<byte[]>(method!.Invoke(null, [challenge]));
        using var document = JsonDocument.Parse(json);

        Assert.Equal("webauthn.create", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(challenge.Challenge, document.RootElement.GetProperty("challenge").GetString());
        Assert.Equal(challenge.Origin, document.RootElement.GetProperty("origin").GetString());
        Assert.False(document.RootElement.GetProperty("crossOrigin").GetBoolean());
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
    }

    private static Type Nested(Type owner, string name) =>
        owner.GetNestedType(name, BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing nested type {name}.");
}
