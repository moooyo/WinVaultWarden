using System.Runtime.InteropServices;

namespace App.Services;

/// <summary>
/// Reads actual package or assembly metadata for diagnostics and release comparison.
/// Update availability is determined separately by the release-check service.
/// </summary>
public static class AboutInfo
{
    public static string AppVersion
    {
        get
        {
            // Package.Current is unavailable for portable processes. Those processes
            // use their compiled release metadata rather than a fixed development value.
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (Exception)
            {
                return ApplicationVersionReader.Read(typeof(AboutInfo).Assembly);
            }
        }
    }

    /// <summary>
    /// Indicates whether the current process has an accessible MSIX package identity.
    /// Diagnostic callers can fall back to portable presentation when it is unavailable.
    /// Security-sensitive registration uses its own explicit package-identity check.
    /// </summary>
    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Windows.ApplicationModel.Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static string WindowsVersion => $"Windows {Environment.OSVersion.Version}";

    public static string DotNetVersion => RuntimeInformation.FrameworkDescription;

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>Builds a plain-text diagnostics block without vault content.</summary>
    public static string ToDiagnosticsText() =>
        global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text3B07C572DF25686C,AppVersion,WindowsVersion,DotNetVersion,Architecture);
}
