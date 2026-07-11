namespace Core.BrowserIntegration;

/// <summary>
/// Single source of truth for where the browser native-messaging Host lives.
/// The Host always ships next to the App entry point (portable root or MSIX install
/// root), so App, installer scripts, and the pipe peer authorizer all resolve the
/// exact same path instead of each guessing an artifacts location.
/// </summary>
public static class BrowserNativeHostLocator
{
    public const string FileName = "BrowserNativeHost.exe";

    /// <summary>Resolves the Host path directly under the supplied App base directory.</summary>
    public static string Resolve(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Path.GetFullPath(Path.Combine(baseDirectory, FileName));
    }

    /// <summary>Resolves the Host path for the currently running process.</summary>
    public static string ResolveCurrent() => Resolve(AppContext.BaseDirectory);
}
