using System.Security.Cryptography;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace App.NativeUiReview;

/// <summary>Starts an isolated, self-driven rendering fixture without normal application startup.</summary>
internal static class NativeUiReviewProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = ReviewOptions.Parse(args);
        Directory.CreateDirectory(options.OutputDirectory);
        global::App.Services.CrashLogger.NativeReviewDirectory = options.OutputDirectory;
        try
        {
            NativeReviewStartupTrace.Record("main-entered");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            NativeReviewStartupTrace.Record("com-wrappers-initialized");
            Application.Start(parameters =>
            {
                try
                {
                    NativeReviewStartupTrace.Record("application-dispatcher-started");
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                    _ = new NativeReviewApplication(options);
                    NativeReviewStartupTrace.Record("application-constructor-returned");
                }
                catch (Exception exception)
                {
                    File.WriteAllText(Path.Combine(options.OutputDirectory, "startup-failure.txt"), exception.ToString());
                    Environment.Exit(1);
                }
            });
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(options.OutputDirectory, "startup-failure.txt"), exception.ToString());
            Environment.ExitCode = 1;
        }
        return Environment.ExitCode;
    }
}

internal sealed partial class NativeReviewApplication : global::App.App
{
    private readonly ReviewOptions _options;
    private readonly ReviewFixture _fixture;
    private MainWindow? _window;

    internal NativeReviewApplication(ReviewOptions options) : this(options, new ReviewFixture(options.LanguagePreference, options.FixtureDataMode)) { }

    private NativeReviewApplication(ReviewOptions options, ReviewFixture fixture) : base(fixture.Services)
    {
        _options = options;
        _fixture = fixture;
        UnhandledException += (_, args) =>
        {
            // Only synthetic fixture state is loaded; XAML diagnostics identify resource and type failures.
            File.AppendAllText(Path.Combine(options.OutputDirectory, "unhandled.txt"),
                $"{DateTimeOffset.UtcNow:O} {args.Exception}{Environment.NewLine}");
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            NativeReviewStartupTrace.Record("application-launched");
            _window = new MainWindow(NativeReviewWindowMarker.Instance);
            NativeReviewStartupTrace.Record("synthetic-window-constructed");
            SetNativeReviewWindow(_window);
            _window.Activate();
            NativeReviewStartupTrace.Record("synthetic-window-activated");
            await new NativeReviewRunner(_options, _fixture, _window).RunAsync();
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            // This entry point loads only synthetic state. Include XAML resource diagnostics
            // and inner errors so a native parse failure can be repaired from concrete evidence.
            File.WriteAllText(Path.Combine(_options.OutputDirectory, "failure.txt"),
                exception.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            var completionPath = Path.Combine(_options.OutputDirectory, "completion-phase.txt");
            File.WriteAllText(completionPath, "quiescing-native-fixture\n");
            _window?.NativeReviewQuiesce();
            await Task.Delay(250);
            _window?.Close();
            File.AppendAllText(completionPath, "native-window-closed\n");
            _fixture.Dispose();
            File.AppendAllText(completionPath, "fixture-disposed\n");
            // A constructor failure can leave no Window whose Close triggers application exit.
            Exit();
        }
    }
}

internal static class NativeReviewStartupTrace
{
    private static readonly Lock Gate = new();
    internal static void Record(string phase)
    {
        var directory = global::App.Services.CrashLogger.NativeReviewDirectory;
        if (string.IsNullOrEmpty(directory)) return;
        lock (Gate)
            File.AppendAllText(Path.Combine(directory, "startup-phases.txt"), $"{DateTimeOffset.UtcNow:O} {phase}{Environment.NewLine}");
    }
}

internal sealed record ReviewOptions(string OutputDirectory, string SourceRoot, string Round, bool EssentialOnly,
    bool PopupProbeOnly, bool LocalizationProbeOnly, string LanguagePreference, string FixtureDataMode)
{
    internal static ReviewOptions Parse(string[] args)
    {
        string? Value(string key)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var output = Value("--output") ?? throw new ArgumentException("An isolated output directory is required.");
        var source = Value("--source-root") ?? throw new ArgumentException("The source root is required.");
        var language = Value("--language") ?? "zh-CN";
        if (language is not ("zh-CN" or "zh-TW" or "en-US" or "ja-JP"))
            throw new ArgumentException("The review requires an explicit supported synthetic language.");
        var dataMode = Value("--data-mode") ?? "full";
        if (dataMode is not ("full" or "empty" or "single-login"))
            throw new ArgumentException("The review requires a declared synthetic data mode.");
        return new ReviewOptions(Path.GetFullPath(output), Path.GetFullPath(source),
            Value("--round") ?? "standalone", args.Contains("--essential", StringComparer.Ordinal),
            args.Contains("--popup-probe", StringComparer.Ordinal), args.Contains("--localization-probe", StringComparer.Ordinal), language, dataMode);
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string ReviewBinaryPath => System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
        ? typeof(NativeReviewRunner).Assembly.Location
        : Environment.ProcessPath ?? throw new InvalidOperationException("The native fixture executable path is unavailable.");

    internal static string ReviewMvid => System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
        ? typeof(NativeReviewRunner).Module.ModuleVersionId.ToString()
        : "unavailable-native-aot";
}
