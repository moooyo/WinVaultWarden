using Api;
using App.Services;
using App.ViewModels;
using Core.Services;
using Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Vault;
using Vault.Porting;

namespace App.Views;

public sealed partial class ImportExportPage : Page
{
    private static readonly string ExportFailureMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1C7CAB143BF6825A);
    private static readonly string UnsupportedCipherCsvExportMessage =
        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5E6195611BA26976);
    private static readonly string ImportReadFailureMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4A65B8539B066BCA);

    public ImportExportViewModel ViewModel { get; }

    private readonly VaultSession _session;
    private readonly IAppLifecycleCoordinator _lifecycle;
    private ImportFormat _lastImportFormat;

    public ImportExportPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<ImportExportViewModel>();
        _session = global::App.App.Services.GetRequiredService<VaultSession>();
        _lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        _lastImportFormat = ViewModel.SelectedImportFormat;
        InitializeComponent();
        ViewModel.RefreshDestinationFolders();
    }

    public static string FormatImportFile(string content, string fileLabel) =>
        string.IsNullOrEmpty(content) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDE583A9B303F6107) : fileLabel;

    public static string FormatChooseFileButton(string content) =>
        string.IsNullOrEmpty(content) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text29D75B8EB8866BB4) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA21A41E49DEB171D);

    public static string FormatImportHint(string content) =>
        string.IsNullOrEmpty(content) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8336AB102651B2C6) : string.Empty;

    public static string FormatImportButton(ImportExportOperationKind? operation) =>
        operation == ImportExportOperationKind.Import ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF6DC1B1641600DD0) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text576D81BB0631B165);

    public static string FormatExportButton(ImportExportOperationKind? operation) =>
        operation == ImportExportOperationKind.Export ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text62731D95BC805803) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text643E7408E6A75D97);

    private void OnImportFormatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: >= 0 } combo)
            return;

        var format = (ImportFormat)combo.SelectedIndex;
        if (format == _lastImportFormat || !ViewModel.CanOperate)
            return;

        _lastImportFormat = format;
        ViewModel.ImportContent = string.Empty;
        ViewModel.ClearPreview();
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var started = ViewModel.BeginOperation(ImportExportOperationKind.Export);
        if (!started.IsSuccess || started.Value is not { } operation)
            return;

        using (operation)
        {
            if (!TryCaptureOperation(out var sessionLease))
            {
                operation.Complete(UiOperationStatus.Cancelled);
                return;
            }

            var passwordBox = new PasswordBox
            {
                Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF36F5F11EDEFB912),
                PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                passwordBox,
                "ExportMasterPasswordBox");

            var errorText = new TextBlock
            {
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                errorText,
                "ExportMasterPasswordErrorText");
            errorText.SetValue(
                TextBlock.ForegroundProperty,
                Application.Current.Resources["SystemFillColorCriticalBrush"]);

            var panel = new StackPanel
            {
                Spacing = 16,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            panel.Children.Add(new TextBlock
            {
                Text = operation.ExportFormat is ExportFormat.Json or ExportFormat.Csv
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text60159E1BA8F9D089)
                    : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0198C91B6AD227C0),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(passwordBox);
            panel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text90C7AA46BD0C326C),
                Content = panel,
                PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text76420433F22D1C80),
                CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot,
                IsPrimaryButtonEnabled = false,
            };
            passwordBox.PasswordChanged += (_, _) =>
            {
                errorText.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(passwordBox.Password);
            };

            try
            {
                while (true)
                {
                    var dialogResult = await DialogHelper.ShowTrackedAsync(
                        dialog,
                        sessionLease,
                        () => passwordBox.Password = string.Empty,
                        operation.Token);
                    if (dialogResult != ContentDialogResult.Primary)
                    {
                        operation.Complete(UiOperationStatus.Cancelled);
                        return;
                    }

                    var verified = await ViewModel.VerifyMasterPasswordAsync(
                        passwordBox.Password,
                        operation.Token);
                    passwordBox.Password = string.Empty;
                    if (!_session.IsUnlocked(sessionLease))
                    {
                        operation.Complete(UiOperationStatus.Cancelled);
                        return;
                    }

                    if (!verified)
                    {
                        errorText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1F643C920B554BEB);
                        errorText.Visibility = Visibility.Visible;
                        continue;
                    }

                    break;
                }

                var format = operation.ExportFormat;
                var exportText = await CreateExportTextAsync(operation, sessionLease);
                if (exportText is null || !_session.IsUnlocked(sessionLease))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return;
                }
                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedFileName = $"bitwarden_export_{DateTime.Now:yyyyMMddHHmm}",
                };
                switch (format)
                {
                    case ExportFormat.Json:
                    case ExportFormat.PasswordProtectedJson:
                    case ExportFormat.AccountRestrictedJson:
                        picker.FileTypeChoices.Add("JSON", [".json"]);
                        break;
                    case ExportFormat.Csv:
                        picker.FileTypeChoices.Add("CSV", [".csv"]);
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported export format.");
                }

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await FilePickerHelper.PickSaveAsync(picker, operation.Token);
                if (file is null || !_session.IsUnlocked(sessionLease))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return;
                }

                var completed = await FilePickerHelper.RunTrackedAsync<bool>(
                    async ct =>
                    {
                        if (!_session.IsUnlocked(sessionLease))
                            return false;

                        using var transaction = await file.OpenTransactedWriteAsync().AsTask(ct);
                        await using var destination = transaction.Stream.AsStreamForWrite();
                        destination.SetLength(0);
                        destination.Position = 0;

                        await using (var writer = new StreamWriter(
                            destination,
                            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                            bufferSize: 8192,
                            leaveOpen: true))
                        {
                            await writer.WriteAsync(exportText.AsMemory(), ct);
                            await writer.FlushAsync(ct);
                        }

                        await destination.FlushAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        if (!_session.IsUnlocked(sessionLease))
                            return false;

                        await FilePickerHelper.CommitSensitiveAsync(
                            transaction,
                            sessionLease,
                            ct);
                        ct.ThrowIfCancellationRequested();
                        return true;
                    },
                    ct: operation.Token);
                if (completed is not true)
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return;
                }

                if (!TryPublish(
                    sessionLease,
                    () => operation.Complete(UiOperationStatus.Succeeded, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextA46E8D59255856E0,file.Name))))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                }
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex))
            {
                operation.Complete(UiOperationStatus.Cancelled);
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                operation.Complete(
                    UiOperationStatus.Failed,
                    UiExceptionPolicy.RequestTimeoutMessage);
            }
            catch (UnsupportedCipherCsvExportException)
            {
                operation.Complete(
                    UiOperationStatus.Failed,
                    UnsupportedCipherCsvExportMessage);
            }
            catch (OrganizationPolicyException)
            {
                operation.Complete(
                    UiOperationStatus.Failed,
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0952F57C2E79C7C8));
            }
            catch (Exception ex) when (IsRecoverableFileOperationException(ex))
            {
                operation.Complete(UiOperationStatus.Failed, ExportFailureMessage);
            }
            finally
            {
                passwordBox.Password = string.Empty;
            }
        }
    }

    private async void OnChooseImportFileClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshDestinationFolders();
        var started = ViewModel.BeginOperation(ImportExportOperationKind.ChooseImportFile);
        if (!started.IsSuccess || started.Value is not { } operation)
            return;

        using (operation)
        {
            if (!TryCaptureOperation(out var sessionLease))
            {
                operation.Complete(UiOperationStatus.Cancelled);
                return;
            }

            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(ImportExportViewModel.ImportFileExtension(operation.ImportFormat));

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await FilePickerHelper.PickSingleAsync(picker, operation.Token);
                if (file is null || !_session.IsUnlocked(sessionLease))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return;
                }

                byte[]? bytes = null;
                byte[]? readBytes = null;
                CancellationToken readToken = default;
                try
                {
                bytes = await FilePickerHelper.RunTrackedAsync(
                    async ct =>
                    {
                        readToken = ct;
                        if (!_session.IsUnlocked(sessionLease))
                            throw ExpiredOperation();

                        ct.ThrowIfCancellationRequested();
                        var properties = await CancelWinsTask.AwaitAsync(
                            file.GetBasicPropertiesAsync().AsTask(),
                            static _ => ValueTask.CompletedTask,
                            ct);
                        if (properties.Size > VaultPortingLimits.MaximumFileSizeBytes)
                            throw new InvalidDataException("The selected backup exceeds the import size limit.");

                        ct.ThrowIfCancellationRequested();
                        using var input = await CancelWinsTask.AwaitAsync(
                            file.OpenReadAsync().AsTask(),
                            static lateStream =>
                            {
                                lateStream.Dispose();
                                return ValueTask.CompletedTask;
                            },
                            ct);
                        await using var source = input.AsStreamForRead();
                        ct.ThrowIfCancellationRequested();
                        readBytes = await CancelWinsTask.AwaitAsync(
                            ReadImportBytesAsync(source, ct),
                            static lateBytes =>
                            {
                                CryptographicOperations.ZeroMemory(lateBytes);
                                return ValueTask.CompletedTask;
                            },
                            ct);
                        return readBytes;
                    },
                    clearSensitive: () =>
                    {
                        if (readToken.IsCancellationRequested && readBytes is not null)
                            CryptographicOperations.ZeroMemory(readBytes);
                    },
                    ct: operation.Token);
                if (bytes is null || !_session.IsUnlocked(sessionLease))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return;
                }

                if (!ImportExportViewModel.TryResolveImportFormat(file.Name, operation.ImportFormat, out var selectedFormat))
                    throw new InvalidDataException("The selected file type does not identify a supported import format.");
                if (selectedFormat == ImportFormat.Json)
                {
                    using var memory = new MemoryStream(bytes, writable: false);
                    using var reader = new StreamReader(memory, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                    var content = await reader.ReadToEndAsync(operation.Token);
                    content = await UnlockImportContentAsync(content, operation, sessionLease);
                    if (content is null || !_session.IsUnlocked(sessionLease))
                    {
                        operation.Complete(UiOperationStatus.Cancelled);
                        return;
                    }
                    CryptographicOperations.ZeroMemory(bytes);
                    bytes = new UTF8Encoding(false, true).GetBytes(content);
                }

                var canonicalJson = await ViewModel.PrepareImportFileAsync(selectedFormat, bytes, operation.Token);
                var preview = await ViewModel.PrepareImportPreviewAsync(operation, canonicalJson);
                if (!_session.IsUnlocked(sessionLease))
                    throw ExpiredOperation();

                if (!TryPublish(
                    sessionLease,
                    () => ViewModel.ApplyPreparedImportFile(operation, file.Name, selectedFormat, canonicalJson, preview)))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                }
                }
                finally
                {
                    if (bytes is not null)
                        CryptographicOperations.ZeroMemory(bytes);
                }
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex))
            {
                operation.Complete(UiOperationStatus.Cancelled);
            }
            catch (Exception ex) when (IsRecoverableFileOperationException(ex))
            {
                operation.Complete(UiOperationStatus.Failed, ImportReadFailureMessage);
            }
        }
    }

    private static Task<byte[]> ReadImportBytesAsync(Stream source, CancellationToken ct) =>
        BoundedImportReader.ReadAsync(source, ct);

    private async Task<string?> CreateExportTextAsync(
        ImportExportOperationScope operation,
        VaultSessionLease sessionLease)
    {
        if (operation.ExportFormat != ExportFormat.PasswordProtectedJson)
            return await ViewModel.ExportToTextAsync(operation.ExportFormat, null, operation.Token);

        var backupPassword = new PasswordBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text84CFFD4304EE3AE6),
            PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text50A65A87586C1E1E),
            MaxLength = 1024,
        };
        var confirmation = new PasswordBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text411443F82B6292E3),
            MaxLength = 1024,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(backupPassword, "ExportBackupPasswordBox");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(confirmation, "ExportBackupPasswordConfirmationBox");
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            errorText,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        errorText.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8E66F1ECF7DD9C65),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(backupPassword);
        panel.Children.Add(confirmation);
        panel.Children.Add(errorText);
        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8FE24017BC5974A6),
            Content = panel,
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text353D7D85CDE29882),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot,
        };
        void ClearPasswords()
        {
            backupPassword.Password = string.Empty;
            confirmation.Password = string.Empty;
        }
        void UpdateValidation(object sender, RoutedEventArgs args)
        {
            dialog.IsPrimaryButtonEnabled = ImportExportViewModel.IsValidBackupPassword(
                backupPassword.Password,
                confirmation.Password);
            errorText.Text = confirmation.Password.Length > 0
                && !string.Equals(backupPassword.Password, confirmation.Password, StringComparison.Ordinal)
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF007E1890A6AA80C)
                    : backupPassword.Password.Length is > 0 and < 12
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text652879DDCEFD860A)
                        : string.Empty;
            errorText.Visibility = errorText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        backupPassword.PasswordChanged += UpdateValidation;
        confirmation.PasswordChanged += UpdateValidation;
        try
        {
            var answer = await DialogHelper.ShowTrackedAsync(dialog, sessionLease, ClearPasswords, operation.Token);
            if (answer != ContentDialogResult.Primary || !_session.IsUnlocked(sessionLease))
                return null;
            if (!ImportExportViewModel.IsValidBackupPassword(backupPassword.Password, confirmation.Password))
                return null;

            var password = backupPassword.Password;
            ClearPasswords();
            try
            {
                return await ViewModel.ExportToTextAsync(operation.ExportFormat, password, operation.Token);
            }
            finally
            {
                password = string.Empty;
            }
        }
        finally
        {
            backupPassword.PasswordChanged -= UpdateValidation;
            confirmation.PasswordChanged -= UpdateValidation;
            ClearPasswords();
        }
    }

    private async Task<string?> UnlockImportContentAsync(
        string content,
        ImportExportOperationScope operation,
        VaultSessionLease sessionLease)
    {
        var protection = ViewModel.InspectImportProtection(content);
        if (protection == ImportProtection.None)
            return content;
        if (protection == ImportProtection.Account)
        {
            var approved = await DialogHelper.ConfirmAsync(
                global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot,
                sessionLease,
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text57043A43A27F358C),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text54A17189C22E2C12),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text439537652398A0F3),
                ct: operation.Token);
            return approved && _session.IsUnlocked(sessionLease)
                ? await ViewModel.UnlockImportJsonAsync(content, null, operation.Token)
                : null;
        }

        var passwordBox = new PasswordBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text84CFFD4304EE3AE6),
            PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C960AC04A092F9B),
            MaxLength = 1024,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(passwordBox, "ImportBackupPasswordBox");
        var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            errorText,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        errorText.SetValue(TextBlock.ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3361B1E5D2658AF4),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(passwordBox);
        panel.Children.Add(errorText);
        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB1093EDAD441EBBF),
            Content = panel,
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text439537652398A0F3),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = global::App.App.MainWindow?.Content?.XamlRoot ?? XamlRoot,
        };
        passwordBox.PasswordChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = passwordBox.Password.Length > 0;
            errorText.Visibility = Visibility.Collapsed;
        };
        try
        {
            while (_session.IsUnlocked(sessionLease))
            {
                var answer = await DialogHelper.ShowTrackedAsync(
                    dialog,
                    sessionLease,
                    () => passwordBox.Password = string.Empty,
                    operation.Token);
                if (answer != ContentDialogResult.Primary || !_session.IsUnlocked(sessionLease))
                    return null;
                var password = passwordBox.Password;
                passwordBox.Password = string.Empty;
                try
                {
                    return await ViewModel.UnlockImportJsonAsync(content, password, operation.Token);
                }
                catch (Exception exception) when (exception is CryptographicException or FormatException)
                {
                    errorText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC98A4A6626F66E3E);
                    errorText.Visibility = Visibility.Visible;
                }
                finally
                {
                    password = string.Empty;
                }
            }
            return null;
        }
        finally
        {
            passwordBox.Password = string.Empty;
        }
    }

    private bool TryCaptureOperation(out VaultSessionLease lease)
    {
        try
        {
            lease = _session.CaptureLease();
            return _session.IsUnlocked(lease);
        }
        catch (InvalidOperationException)
        {
            lease = null!;
            return false;
        }
    }

    private static OperationCanceledException ExpiredOperation() =>
        new(
            "The vault session changed before the file operation completed.",
            new CancellationToken(canceled: true));

    private static bool IsRecoverableFileOperationException(Exception exception) =>
        RequestTimeoutClassifier.IsTimeout(exception)
        || exception is IOException
            or UnauthorizedAccessException
            or HttpRequestException
            or TimeoutException
            or AccountOperationException
            or FormatException
            or DecoderFallbackException
            or CryptographicException
            or System.Text.Json.JsonException
            or NotSupportedException
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException;

    private bool TryPublish(VaultSessionLease lease, Action publish) =>
        _lifecycle.TryRunSensitive(
            () => _session.TryRunIfUnlocked(lease, publish));
}
