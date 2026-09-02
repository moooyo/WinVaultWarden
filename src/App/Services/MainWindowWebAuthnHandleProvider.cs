namespace App.Services;

public sealed class MainWindowWebAuthnHandleProvider : IWebAuthnWindowHandleProvider
{
    public nint GetWindowHandle() => global::App.App.MainWindow is { } window
        ? WinRT.Interop.WindowNative.GetWindowHandle(window)
        : nint.Zero;
}
