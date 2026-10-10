using Api;
using App.Services;
using App.ViewModels;
using Core.Services;
using Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Vault;
using Vault.Porting;

namespace App.Views;

public sealed partial class ImportExportPage : Page
{
    private const string ExportFailureMessage = "导出失败，请稍后重试。";
    private const string UnsupportedCipherCsvExportMessage =
        "密码库包含 CSV 不支持的条目类型。为避免数据丢失，已取消导出；请改用 JSON 格式。";
    private const string ImportReadFailureMessage = "无法读取所选文件，请确认文件可用后重试。";

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
    }

    public static string FormatImportFile(string content, string fileLabel) =>
        string.IsNullOrEmpty(content) ? "还没有选择文件" : fileLabel;

    public static string FormatChooseFileButton(string content) =>
        string.IsNullOrEmpty(content) ? "选择文件…" : "更换…";

    public static string FormatImportHint(string content) =>
        string.IsNullOrEmpty(content) ? "先选择要导入的文件" : string.Empty;

    public static string FormatImportButton(ImportExportOperationKind? operation) =>
        operation == ImportExportOperationKind.Import ? "正在导入…" : "导入";

    public static string FormatExportButton(ImportExportOperationKind? operation) =>
        operation == ImportExportOperationKind.Export ? "正在导出…" : "导出…";

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
                Header = "主密码",
                PlaceholderText = "当前主密码",
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
                Text = "文件不加密，请妥善保管，用完尽快删除。导出前需要验证主密码。",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(passwordBox);
            panel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = "导出密码库",
                Content = panel,
                PrimaryButtonText = "导出",
                CloseButtonText = "取消",
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
                        errorText.Text = "主密码不正确";
                        errorText.Visibility = Visibility.Visible;
                        continue;
                    }

                    break;
                }

                var format = operation.ExportFormat;
                var exportText = ViewModel.ExportToText(format);
                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedFileName = $"bitwarden_export_{DateTime.Now:yyyyMMddHHmm}",
                };
                switch (format)
                {
                    case ExportFormat.Json:
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
                    () => operation.Complete(UiOperationStatus.Succeeded, $"已导出到“{file.Name}”。")))
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
                    "组织策略已禁用个人密码库导出。");
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
                picker.FileTypeFilter.Add(operation.ImportFormat == ImportFormat.Json ? ".json" : ".csv");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await FilePickerHelper.PickSingleAsync(picker, operation.Token);
                if (file is null || !_session.IsUnlocked(sessionLease))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return;
                }

                var content = await FilePickerHelper.RunTrackedAsync(
                    async ct =>
                    {
                        if (!_session.IsUnlocked(sessionLease))
                            throw ExpiredOperation();

                        var readTask = Windows.Storage.FileIO.ReadTextAsync(file).AsTask(ct);
                        var text = await CancelWinsTask.AwaitAsync(
                            readTask,
                            static _ => ValueTask.CompletedTask,
                            ct);
                        ct.ThrowIfCancellationRequested();
                        if (!_session.IsUnlocked(sessionLease))
                            throw ExpiredOperation();
                        return text;
                    },
                    ct: operation.Token);
                if (content is null)
                {
                    operation.Complete(UiOperationStatus.Cancelled);
                    return;
                }

                if (!TryPublish(
                    sessionLease,
                    () => ViewModel.ApplySelectedImportFile(operation, file.Name, content)))
                {
                    operation.Complete(UiOperationStatus.Cancelled);
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
            or ObjectDisposedException
            or System.Runtime.InteropServices.COMException;

    private bool TryPublish(VaultSessionLease lease, Action publish) =>
        _lifecycle.TryRunSensitive(
            () => _session.TryRunIfUnlocked(lease, publish));
}
