using App.ViewModels.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    private void OnChooseFileClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Draft.FileName))
            Draft.FileName = "本地文件.dat";
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Saved = Draft.HasRequiredData();
        args.Cancel = !Saved;
    }
}
