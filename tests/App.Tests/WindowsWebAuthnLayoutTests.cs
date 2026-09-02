using System.Reflection;
using System.Runtime.InteropServices;
using App.Services;
using Xunit;

namespace App.Tests;

public sealed class WindowsWebAuthnLayoutTests
{
    [Fact]
    public void StableWebAuthnPrefixesMatchWindowsSdkLayoutOn64Bit()
    {
        if (IntPtr.Size != 8)
            return;

        var service = typeof(WindowsWebAuthnTwoFactorService);
        var credential = Nested(service, "WebAuthnCredential");
        var clientData = Nested(service, "WebAuthnClientData");
        var options = Nested(service, "WebAuthnGetAssertionOptionsV3");
        var assertion = Nested(service, "WebAuthnAssertionV1");

        Assert.Equal(24, Marshal.SizeOf(credential));
        Assert.Equal(24, Marshal.SizeOf(clientData));
        Assert.Equal(80, Marshal.SizeOf(options));
        Assert.Equal(72, Marshal.SizeOf(assertion));
        Assert.Equal(56, Marshal.OffsetOf(options, "U2fAppId").ToInt32());
        Assert.Equal(72, Marshal.OffsetOf(options, "CancellationId").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf(assertion, "Credential").ToInt32());
        Assert.Equal(64, Marshal.OffsetOf(assertion, "UserId").ToInt32());
    }

    [Fact]
    public void IsSupported_IsFalseOutsideWindowsWithoutLoadingNativeLibrary()
    {
        if (OperatingSystem.IsWindows())
            return;

        var service = new WindowsWebAuthnTwoFactorService(new FakeWindowHandle());

        Assert.False(service.IsSupported);
    }

    private static Type Nested(Type owner, string name) =>
        owner.GetNestedType(name, BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing native layout {name}.");

    private sealed class FakeWindowHandle : IWebAuthnWindowHandleProvider
    {
        public nint GetWindowHandle() => (nint)1;
    }
}
