using Xunit;
using System.Runtime.CompilerServices;
using App.Services;

namespace WindowsPlatform.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This synthetic native test requires Windows.";
    }
}

public sealed class WindowsPluginFactAttribute : FactAttribute
{
    public WindowsPluginFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This synthetic native test requires Windows.";
        else if (!WindowsPasskeyPlatformApi.GetStatus().Supported)
            Skip = "The Windows WebAuthn plugin decoder API is unavailable on this OS build.";
    }
}
