using System.Runtime.CompilerServices;
using Xunit;

namespace BrowserNativeHost.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This integration test requires Windows path semantics.";
    }
}
