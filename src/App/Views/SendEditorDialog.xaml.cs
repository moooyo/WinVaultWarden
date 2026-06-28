using App.ViewModels.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices.WindowsRuntime;

namespace App.Views;

public sealed partial class SendEditorDialog : ContentDialog
{
    private bool _canUpdateBindings;

    public SendEditorDraft Draft { get; }
    public bool Saved { get; private set; }
    public bool IsEditMode { get; }
    private readonly SendListItem? _existing;

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

    public SendEditorDialog()
    {
        Draft = SendEditorDraft.CreateDefault(SendType.Text);
        InitializeComponent();
        Loaded += (_, _) => _canUpdateBindings = true;
    }

    public SendEditorDialog(SendListItem existing)
    {
        _existing = existing;
        IsEditMode = true;
        Draft = SendEditorDraft.FromExisting(existing);
        InitializeComponent();
        PrimaryButtonText = "保存";
        Loaded += (_, _) =>
        {
            _canUpdateBindings = true;
            Bindings?.Update();
        };
    }

    public SendListItem? Existing => _existing;

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

    private async void OnChooseFileClick(object sender, RoutedEventArgs e)
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

        var file = await picker.PickSingleFileAsync();
        if (file is null)
            return;

        var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
        Draft.FileBytes = buffer.ToArray();
        Draft.FileName = file.Name;
        if (string.IsNullOrWhiteSpace(Draft.Name))
            Draft.Name = file.Name;
        Bindings?.Update();
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Saved = Draft.HasRequiredData();
        args.Cancel = !Saved;
    }
}
