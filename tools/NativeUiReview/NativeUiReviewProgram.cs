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
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new NativeReviewApplication(options);
        });
        return Environment.ExitCode;
    }
}

internal sealed partial class NativeReviewApplication : global::App.App
{
    private readonly ReviewOptions _options;
    private readonly ReviewFixture _fixture;
    private MainWindow? _window;

    internal NativeReviewApplication(ReviewOptions options) : this(options, new ReviewFixture()) { }

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
            _window = new MainWindow(NativeReviewWindowMarker.Instance);
            SetNativeReviewWindow(_window);
            _window.Activate();
            await new NativeReviewRunner(_options, _fixture, _window).RunAsync();
            Environment.ExitCode = 0;
        }
        catch (Exception exception)
        {
            // The input domain is synthetic; omit messages to keep diagnostics narrowly scoped.
            File.WriteAllText(Path.Combine(_options.OutputDirectory, "failure.txt"),
                $"{exception.GetType().FullName} 0x{exception.HResult:X8}{Environment.NewLine}{exception.StackTrace}");
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
        }
    }
}

internal sealed record ReviewOptions(string OutputDirectory, string SourceRoot, string Round, bool EssentialOnly, bool PopupProbeOnly)
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
        return new ReviewOptions(Path.GetFullPath(output), Path.GetFullPath(source),
            Value("--round") ?? "standalone", args.Contains("--essential", StringComparer.Ordinal),
            args.Contains("--popup-probe", StringComparer.Ordinal));
    }

    internal static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
