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
    private bool _canApplyExpirationChanges;
    private readonly Func<SendEditorDraft, CancellationToken, Task<UiOperationResult>> _save;

    public SendEditorDraft Draft { get; }
    public bool IsEditMode { get; }
    public bool CanChangeType => !IsEditMode;

    public string DialogTitle => IsEditMode
        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE860D7675952CD43)
        : (Draft.Type == SendType.File ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text767E0279344337A2) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF327B420BC2B658E));

    public double MaxAccessCountValue
    {
        get => Draft.MaxAccessCount ?? double.NaN;
        set => Draft.MaxAccessCount = double.IsNaN(value) ? null : (int)Math.Clamp(Math.Round(value), 0, int.MaxValue);
    }

    public bool ShowCustomDeletionPicker => Draft.IsCustomDeletion;

    public DateTimeOffset CustomDeletionMinDate => DateTimeOffset.Now;
    public DateTimeOffset CustomDeletionMaxDate => DateTimeOffset.Now.AddDays(31);

    // Complete the caller-provided save operation inside the primary-button deferral and keep failures inline.
    public SendEditorDialog(
        SendEditorDraft draft,
        bool isEditMode,
        Func<SendEditorDraft, CancellationToken, Task<UiOperationResult>> save)
    {
        Draft = draft;
        IsEditMode = isEditMode;
        _save = save;
        InitializeComponent();
        FillDateChoices(DeletionPresetChoice, SendDatePresetOptions.CreateDeletionOptions(compact: true), Draft.DeletionPreset);
        FillDateChoices(ExpirationPresetChoice, SendDatePresetOptions.CreateExpirationOptions(compact: true), Draft.ExpirationPreset);
        if (isEditMode)
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextECF0EA01192D5A01);
        Loaded += (_, _) =>
        {
            _canUpdateBindings = true;
            Bindings?.Update();
            _canApplyExpirationChanges = true;
        };
        Opened += (_, _) =>
        {
            ApplyResponsiveWidth();
            SendNameTextBox.Focus(FocusState.Programmatic);
        };
        SizeChanged += (_, _) => ApplyResponsiveWidth();
    }

    // Clamp the form to the XamlRoot width to avoid horizontal clipping at high DPI or narrow sizes.
    private void ApplyResponsiveWidth()
    {
        var rootWidth = XamlRoot?.Size.Width ?? 0;
        SendEditorContent.Width = ResponsiveDialogSizing.Clamp(
            rootWidth,
            preferredWidth: 520,
            horizontalMargin: 48);
        SendEditorScrollViewer.MaxHeight = Math.Clamp((XamlRoot?.Size.Height ?? 800) - 220, 120, 620);
    }

    private void OnTextChecked(object sender, RoutedEventArgs e) => SetType(SendType.Text);

    private void OnFileChecked(object sender, RoutedEventArgs e) => SetType(SendType.File);

    private void SetType(SendType type)
    {
        if (IsEditMode)
            return;
        SendEditorDialogLifecycle.SetType(
            Draft,
            type,
            _canUpdateBindings,
            Bindings is null ? null : Bindings.Update);
    }

    private void OnDeletionLabelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_canApplyExpirationChanges
            || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: SendDatePreset preset } })
            return;
        Draft.SelectDeletion(preset, DateTimeOffset.UtcNow);
        Bindings?.Update();
    }

    private void OnCustomDeletionDateChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        if (!_canApplyExpirationChanges || args.NewDate is not { } selectedDate)
            return;
        Draft.SelectCustomDeletionDate(selectedDate, TimeZoneInfo.Local);
        Bindings?.Update();
    }

    private void OnExpirationLabelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_canApplyExpirationChanges
            || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: SendDatePreset preset } })
            return;

        Draft.SelectExpiration(preset, DateTimeOffset.UtcNow);
        Bindings?.Update();
    }

    private static void FillDateChoices(
        ComboBox combo,
        IReadOnlyList<SendDatePresetOption> options,
        SendDatePreset selected)
    {
        foreach (var option in options)
        {
            var item = new ComboBoxItem { Content = option.Label, Tag = option.Value };
            combo.Items.Add(item);
            if (option.Value == selected)
                combo.SelectedItem = item;
        }
    }

    private void OnCustomExpirationDateChanged(
        CalendarDatePicker sender,
        CalendarDatePickerDateChangedEventArgs args)
    {
        if (!_canApplyExpirationChanges)
            return;

        if (args.NewDate is { } selectedDate)
            Draft.SelectCustomExpirationDate(selectedDate, TimeZoneInfo.Local);

        Bindings?.Update();
    }

    // Record only the selected file name and length; encryption reopens the source without caching plaintext.
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
            ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFC35EAAAAD04F005));
        }
        catch (UnauthorizedAccessException)
        {
            ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA2DAD4AAC80F11EE));
        }
        catch (IOException)
        {
            ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text51BDBEFBC0C88421));
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

        // Attach the desktop picker to this window's HWND.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::App.App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return picker;
    }

    private void ShowEditorError(string message)
    {
        SendEditorErrorInfoBar.Message = message;
        SendEditorErrorInfoBar.IsOpen = true;
    }

    // Keep the dialog open for failed, busy, or cancelled saves and close only after success.
    // Transient tracking cancels the save and clears sensitive values during lock or sign-out.
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
                ShowEditorError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC8DF7D05EC1505D7));
                args.Cancel = true;
                return;
            }

            IsPrimaryButtonEnabled = false;
            var result = await _save(Draft, saveCts.Token);
            if (result.Status == UiOperationStatus.Failed)
            {
                ShowEditorError(result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8715DD8F2827D9A3));
                args.Cancel = true;
                return;
            }
            if (!result.IsSuccess)
            {
                // Busy or cancelled operations preserve the draft and file source for a retry.
                args.Cancel = true;
                return;
            }
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }
}
