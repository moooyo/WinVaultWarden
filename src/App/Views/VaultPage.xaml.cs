using System;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace App.Views;

public sealed partial class VaultPage : Page
{
    public VaultViewModel ViewModel { get; }

    public VaultPage()
    {
        // 用完全限定名避免与命名空间 App.Services 冲突(App 类的静态属性 Services)。
        ViewModel = global::App.App.Services.GetRequiredService<VaultViewModel>();
        InitializeComponent();
        GroupedItemsSource.Source = ViewModel.GroupedItems;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FoldersChanged += OnVaultFoldersChanged;
        UpdateDetailTemplate();
    }

    public static Visibility VisibleIfTrue(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static ListViewSelectionMode SelectionModeFromBool(bool isSelectionMode) =>
        isSelectionMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;

    private static void SetRowActionsOpacity(object sender, double opacity)
    {
        if (sender is Grid root && root.FindName("RowActions") is FrameworkElement panel)
        {
            panel.Opacity = opacity;
            panel.IsHitTestVisible = opacity > 0;
        }
    }

    private void OnRowPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetRowActionsOpacity(sender, 1);
    private void OnRowPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetRowActionsOpacity(sender, 0);
    private void OnRowGotFocus(object sender, RoutedEventArgs e) => SetRowActionsOpacity(sender, 1);
    private void OnRowLostFocus(object sender, RoutedEventArgs e) => SetRowActionsOpacity(sender, 0);

    private static CipherListItem? RowItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as CipherListItem;

    private void OnRowEditClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
        {
            ViewModel.BeginEdit(item.Id);
            SyncEditorTypeSelection();
        }
    }

    private async void OnRowFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await ViewModel.ToggleFavoriteAsync(item.Id);
    }

    private async void OnRowSoftDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item
            && await ConfirmAsync("移到回收站", $"确定要将“{item.Name}”移到回收站吗?", "删除"))
            await ViewModel.SoftDeleteAsync(item.Id);
    }

    private async void OnRowRestoreClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item)
            await ViewModel.RestoreAsync(item.Id);
    }

    private async void OnRowPermanentDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowItem(sender) is { } item
            && await ConfirmAsync("永久删除", $"确定要永久删除“{item.Name}”吗?此操作无法撤销。", "永久删除"))
            await ViewModel.PermanentDeleteAsync(item.Id);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string tag)
            ViewModel.SelectFilterByTag(tag);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultViewModel.Detail))
            UpdateDetailTemplate();
    }

    private void OnAddLoginClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Login);

    private void OnAddCardClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Card);

    private void OnAddIdentityClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Identity);

    private void OnAddNoteClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Note);

    private void OnAddSshClick(object sender, RoutedEventArgs e) => BeginAdd(VaultItemKind.Ssh);

    private void BeginAdd(VaultItemKind kind)
    {
        ViewModel.BeginAdd(kind);
        SyncEditorTypeSelection();
    }

    private void OnCancelCipherEditorClick(object sender, RoutedEventArgs e) => ViewModel.CancelEdit();

    private async void OnSaveCipherEditorClick(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.SaveDraftAsync())
            UpdateDetailTemplate();
    }

    private void OnCipherEditorTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ViewModel.IsEditing || CipherEditorTypeBox.SelectedItem is not ComboBoxItem item)
            return;

        if (item.Tag is string tag && Enum.TryParse(tag, out VaultItemKind kind))
        {
            ViewModel.ChangeEditorType(kind);
        }
    }

    private void SyncEditorTypeSelection()
    {
        if (ViewModel.EditorDraft is null)
            return;

        var tag = ViewModel.EditorDraft.Type.ToString();
        foreach (var item in CipherEditorTypeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag as string == tag)
            {
                CipherEditorTypeBox.SelectedItem = item;
                return;
            }
        }
    }

    // 按 Detail 运行时类型从 Page.Resources 取对应 DataTemplate。
    private void UpdateDetailTemplate()
    {
        var detail = ViewModel.Detail;
        if (detail is null)
        {
            DetailHost.Content = null;
            DetailHost.ContentTemplate = null;
            return;
        }

        var key = detail switch
        {
            LoginDetail => "LoginTemplate",
            CardDetail => "CardTemplate",
            IdentityDetail => "IdentityTemplate",
            NoteDetail => "NoteTemplate",
            SshDetail => "SshTemplate",
            _ => null,
        };

        DetailHost.ContentTemplate = key is not null ? Resources[key] as DataTemplate : null;
        DetailHost.Content = detail;
    }

    private void OnVaultFoldersChanged(object? sender, EventArgs e) =>
        global::App.App.MainWindow?.RefreshFolderNavigation();

    private void OnEditCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
        {
            ViewModel.BeginEdit(item.Id);
            SyncEditorTypeSelection();
        }
    }

    private async void OnDeleteCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
            return;
        if (await ConfirmAsync("移到回收站", $"确定要将“{item.Name}”移到回收站吗?", "删除"))
            await ViewModel.SoftDeleteAsync(item.Id);
    }

    private async void OnRestoreCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item)
            await ViewModel.RestoreAsync(item.Id);
    }

    private async void OnPermanentDeleteCipherClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item)
            return;
        if (await ConfirmAsync("永久删除", $"确定要永久删除“{item.Name}”吗?此操作无法撤销。", "永久删除"))
            await ViewModel.PermanentDeleteAsync(item.Id);
    }

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var name = await PromptTextAsync("新建文件夹", "文件夹名称", string.Empty);
        if (!string.IsNullOrWhiteSpace(name))
            await ViewModel.SaveFolderAsync(null, name!);
    }

    private async void OnRenameFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFilter is not { Kind: FilterKind.Folder } folder || folder.FolderId is null)
            return;
        var name = await PromptTextAsync("重命名文件夹", "文件夹名称", folder.Label);
        if (!string.IsNullOrWhiteSpace(name))
            await ViewModel.SaveFolderAsync(folder.FolderId, name!);
    }

    private async void OnDeleteFolderClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedFilter is not { Kind: FilterKind.Folder } folder || folder.FolderId is null)
            return;
        if (await ConfirmAsync("删除文件夹", $"确定要删除文件夹“{folder.Label}”吗?其中的项目会移出该文件夹。", "删除"))
            await ViewModel.DeleteFolderAsync(folder.FolderId);
    }

    private Task<bool> ConfirmAsync(string title, string message, string primaryText) =>
        DialogHelper.ConfirmAsync(XamlRoot, title, message, primaryText);

    private async Task<string?> PromptTextAsync(string title, string placeholder, string initial)
    {
        var input = new TextBox { PlaceholderText = placeholder, Text = initial };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }
}
