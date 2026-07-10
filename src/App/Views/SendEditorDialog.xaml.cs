using System.IO;
using App.Services;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class SendEditorDialog : ContentDialog
{
    private bool _canUpdateBindings;
    private readonly Func<SendEditorDraft, CancellationToken, Task<UiOperationResult>> _save;

    public SendEditorDraft Draft { get; }
    public bool Saved { get; private set; }
    public bool IsEditMode { get; }

    public string DialogTitle => IsEditMode
        ? "编辑 Send"
        : (Draft.Type == SendType.File ? "新增文件 Send" : "新增文本 Send");

    public double MaxAccessCountValue
    {
        get => Draft.MaxAccessCount ?? 0;
        set => Draft.MaxAccessCount = value <= 0 ? null : (int)Math.Round(value);
    }

    public bool ShowCustomDeletionPicker => Draft.DeletionDateLabel == "自定义";

    public DateTimeOffset CustomDeletionMinDate => DateTimeOffset.Now;
    public DateTimeOffset CustomDeletionMaxDate => DateTimeOffset.Now.AddDays(31);

    // 保存在 Primary deferral 内完成:接收调用方提供的 save 委托(创建/更新),失败保留对话框并内联报错。
    public SendEditorDialog(
        SendEditorDraft draft,
        bool isEditMode,
        Func<SendEditorDraft, CancellationToken, Task<UiOperationResult>> save)
    {
        Draft = draft;
        IsEditMode = isEditMode;
        _save = save;
        InitializeComponent();
        if (isEditMode)
            PrimaryButtonText = "保存";
        Loaded += (_, _) =>
        {
            _canUpdateBindings = true;
            Bindings?.Update();
        };
    }

    private void OnTextChecked(object sender, RoutedEventArgs e) => SetType(SendType.Text);

    private void OnFileChecked(object sender, RoutedEventArgs e) => SetType(SendType.File);

    private void SetType(SendType type)
    {
        SendEditorDialogLifecycle.SetType(
            Draft,
            type,
            _canUpdateBindings,
            Bindings is null ? null : Bindings.Update);
    }

    private void OnDeletionLabelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_canUpdateBindings)
            Bindings?.Update();
    }

    // 过期时间相对标签 ⇄ Draft.ExpirationDate(可空)。
    public string ExpirationLabel
    {
        get => Draft.ExpirationDate is null ? "永不" : _expirationLabel;
        set
        {
            _expirationLabel = value;
            Draft.ExpirationDate = value switch
            {
                "1 小时" => DateTimeOffset.UtcNow.AddHours(1),
                "1 天" => DateTimeOffset.UtcNow.AddDays(1),
                "7 天" => DateTimeOffset.UtcNow.AddDays(7),
                _ => null,
            };
        }
    }
    private string _expirationLabel = "永不";

    // 选择文件:只读取名称/长度构造可重开 SendFileSource(超限在构造前抛出),绝不读取整份明文字节。
    private async void OnChooseFileClick(object sender, RoutedEventArgs e)
    {
        var picker = CreateFilePicker();
        var file = await FilePickerHelper.PickSingleAsync(picker);
        if (file is null)
            return;

        try
        {
            Draft.ReplacementFile = await StorageFileSendSource.CreateAsync(file);
            Draft.FileName = file.Name;
            if (string.IsNullOrWhiteSpace(Draft.Name))
                Draft.Name = file.Name;
            SendEditorErrorInfoBar.IsOpen = false;
            Bindings?.Update();
        }
        catch (ArgumentOutOfRangeException)
        {
            ShowEditorError("文件超过 500 MiB 上限。");
        }
        catch (UnauthorizedAccessException)
        {
            ShowEditorError("没有读取所选文件的权限。");
        }
        catch (IOException)
        {
            ShowEditorError("无法读取所选文件，请重新选择。");
        }
    }

    private static Windows.Storage.Pickers.FileOpenPicker CreateFilePicker()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add("*");

        // WinUI 3 桌面应用:必须把 picker 关联到本窗口的 HWND。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return picker;
    }

    private void ShowEditorError(string message)
    {
        SendEditorErrorInfoBar.Message = message;
        SendEditorErrorInfoBar.IsOpen = true;
    }

    // Primary 保存:deferral 内完成上传/写入。失败(Failed)保留对话框并展示可重试错误;Busy/Cancelled 同样保留
    // 对话框但不显示错误(瞬态);成功才置 Saved 并关闭。全程注册到瞬态登记表,使锁屏/登出能取消并清空敏感值。
    private async void OnPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        using var saveCts = new CancellationTokenSource();
        using var tracked = global::App.App.Services
            .GetRequiredService<ITransientOperationRegistry>()
            .Register(saveCts.Cancel, Hide, Draft.ClearSensitive);
        try
        {
            if (!Draft.HasRequiredData()
                || (!IsEditMode && Draft.IsFile && Draft.ReplacementFile is null))
            {
                ShowEditorError("请填写必填项并为文件 Send 选择文件。");
                args.Cancel = true;
                return;
            }

            IsPrimaryButtonEnabled = false;
            var result = await _save(Draft, saveCts.Token);
            if (result.Status == UiOperationStatus.Failed)
            {
                ShowEditorError(result.Error ?? "保存 Send 失败，请重试。");
                args.Cancel = true;
                return;
            }
            if (!result.IsSuccess)
            {
                // Busy/Cancelled:保留对话框而不显示错误(瞬态);草稿与已选文件源保持不变以便重试。
                args.Cancel = true;
                return;
            }
            Saved = true;
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }
}
