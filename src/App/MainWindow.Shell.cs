using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using App.Views;
using Core.Services;
using Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;

namespace App;

public sealed partial class MainWindow
{
    private string _shellPage = string.Empty;
    private string _contextTag = string.Empty;
    private bool _searchUpdating;
    private bool _compactSearchOpen;
    private bool _syncing;
    private DateTimeOffset? _lastSyncAt;
    private DateTimeOffset? _noticeUntil;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusTimer;
    private IClipboardStatus? _clipboardStatus;
    private ISensitiveActionVerificationService? _sensitiveActions;
    private readonly UISettings _uiSettings = new();
    private bool _historyExpanded;
    private VaultPage? _cachedVaultPage;
    private VaultViewModel? _observedVaultViewModel;
    private SendPage? _cachedSendPage;
    private Storyboard? _tabIndicatorAnimation;
    private double _tabIndicatorTarget = -1;
    private readonly Dictionary<Button, long> _historyCopyButtons = new();
    private bool _sendRefreshQueued;
    private int _lastPendingRequestCount = -1;
    private NavigationViewItem? _compactActionItem;
    private NavigationViewItem? _folderGroupHeader;
    private Button? _folderAddButton;
    private double _lastShellWidth;
    private bool _compactTopTabs;
    private double _expandedTopTabsWidth;
    private readonly Dictionary<ToggleButton, object?> _expandedTopTabContents = [];

    private void InitializeShell()
    {
        foreach (var tab in new[] { VaultTab, GeneratorTab, SendTab, ReportsTab })
        {
            _expandedTopTabContents[tab] = tab.Content;
            var label = tab.Content is StackPanel panel
                ? panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text
                : tab.Content as string;
            if (!string.IsNullOrWhiteSpace(label))
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab, label);
                ToolTipService.SetToolTip(tab, label);
            }
        }
        TopTabs.SizeChanged += (_, args) =>
        {
            if (!_compactTopTabs && args.NewSize.Width > 0)
                _expandedTopTabsWidth = args.NewSize.Width;
            UpdateResponsiveShell();
        };
        BrandPanel.SizeChanged += (_, _) => UpdateResponsiveShell();
        TitleActions.SizeChanged += (_, _) => UpdateResponsiveShell();
        Nav.Loaded += (_, _) => UpdateResponsiveShell();
        _clipboardStatus = global::App.App.Services.GetRequiredService<IClipboardService>() as IClipboardStatus;
        _sensitiveActions = global::App.App.Services.GetRequiredService<ISensitiveActionVerificationService>();
        if (_clipboardStatus is not null)
            _clipboardStatus.Changed += OnShellStatusChanged;
        _sensitiveActions.GracePeriodChanged += OnShellStatusChanged;
        var requests = global::App.App.Services.GetRequiredService<DeviceApprovalWindowService>();
        requests.UnlockAsync = UnlockForDeviceApprovalAsync;
        requests.NoticeRaised += ShowNotice;
        requests.PendingRequestsChanged += OnPendingRequestsChanged;
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();
        var find = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        find.Invoked += (_, args) =>
        {
            if (IsVaultUnlocked() && Nav.IsEnabled
                && !HasOpenPopup())
            {
                FocusVaultSearch();
                args.Handled = true;
            }
        };
        ShellRoot.KeyboardAccelerators.Add(find);
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        Nav.PaneOpened += (_, _) => UpdateResponsiveShell();
        Nav.PaneClosed += (_, _) => UpdateResponsiveShell();
        ShellRoot.ActualThemeChanged += (_, _) => UpdateThemeIcon();
        global::App.App.Services.GetRequiredService<SecurityReportViewModel>().PropertyChanged += OnReportChanged;
        global::App.App.Services.GetRequiredService<GeneratorViewModel>().History.CollectionChanged += OnGeneratorHistoryChanged;
        Closed += (_, _) =>
        {
            _statusTimer?.Stop();
            if (_clipboardStatus is not null)
                _clipboardStatus.Changed -= OnShellStatusChanged;
            _sensitiveActions.GracePeriodChanged -= OnShellStatusChanged;
            requests.NoticeRaised -= ShowNotice;
            requests.PendingRequestsChanged -= OnPendingRequestsChanged;
            global::App.App.Services.GetRequiredService<SecurityReportViewModel>().PropertyChanged -= OnReportChanged;
            global::App.App.Services.GetRequiredService<GeneratorViewModel>().History.CollectionChanged -= OnGeneratorHistoryChanged;
            ObserveVaultViewModel(null);
            if (_cachedSendPage is not null)
            {
                _cachedSendPage.ViewModel.Items.CollectionChanged -= OnSendItemsChanged;
                _cachedSendPage.ContextChanged -= OnSendContextChanged;
            }
        };
    }

    private void OnReportChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateNavigationCounts);

    private void OnPendingRequestsChanged() => DispatcherQueue.TryEnqueue(UpdateNavigationCounts);

    private void OnVaultOperationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(VaultViewModel.CanCancelEditor))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(sender, _observedVaultViewModel))
                    UpdateFolderActionState();
            });
    }

    private void ObserveVaultViewModel(VaultViewModel? viewModel)
    {
        if (!ReferenceEquals(_observedVaultViewModel, viewModel))
        {
            if (_observedVaultViewModel is not null)
                _observedVaultViewModel.PropertyChanged -= OnVaultOperationChanged;
            _observedVaultViewModel = viewModel;
            if (_observedVaultViewModel is not null)
                _observedVaultViewModel.PropertyChanged += OnVaultOperationChanged;
        }

        UpdateFolderActionState();
    }

    private void UpdateFolderActionState()
    {
        if (_folderAddButton is null)
            return;

        var currentVault = ContentFrame.Content as VaultPage;
        var isCurrent = currentVault is not null
            && ReferenceEquals(currentVault.ViewModel, _observedVaultViewModel);
        var available = IsVaultUnlocked() && isCurrent
            && _observedVaultViewModel?.CanCancelEditor == true;
        var description = available
            ? "Create a folder in the current vault."
            : "Finish or reconcile the current vault operation before creating a folder.";

        _folderAddButton.IsEnabled = available;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(_folderAddButton, description);
        ToolTipService.SetToolTip(_folderAddButton, description);
    }

    private void OnSendItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_sendRefreshQueued) return;
        _sendRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _sendRefreshQueued = false;
            if (_shellPage == "send") RefreshShellNavigation();
        });
    }

    private void OnSendContextChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsVaultUnlocked() && ReferenceEquals(sender, _cachedSendPage)
            && ReferenceEquals(sender, ContentFrame.Content))
            RefreshShellNavigation();
    });

    private void OnGeneratorHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_shellPage == "gen")
            DispatcherQueue.TryEnqueue(() => RefreshShellNavigation(force: true));
    }

    private void SetShellSessionVisibility(bool unlocked)
    {
        var visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;
        TopTabs.Visibility = visibility;
        TitleSearchHost.Visibility = visibility;
        LockButton.Visibility = visibility;
        SettingsButton.Visibility = visibility;
        AccountButton.Visibility = visibility;
        StatusBar.Visibility = visibility;
        if (!unlocked)
        {
            _searchUpdating = true;
            TitleSearch.Text = string.Empty;
            _searchUpdating = false;
            _compactSearchOpen = false;
            _shellPage = string.Empty;
            _contextTag = string.Empty;
            _lastSyncAt = null;
            ObserveVaultViewModel(null);
            _cachedVaultPage = null;
            if (_cachedSendPage is not null)
            {
                _cachedSendPage.ViewModel.Items.CollectionChanged -= OnSendItemsChanged;
                _cachedSendPage.ContextChanged -= OnSendContextChanged;
            }
            _cachedSendPage = null;
            _historyCopyButtons.Clear();
            Nav.MenuItems.Clear();
            Nav.FooterMenuItems.Clear();
            _folderGroupHeader = null;
            _folderAddButton = null;
            AccountNameText.Text = string.Empty;
            NoticeText.Text = string.Empty;
            ClipboardStatusText.Text = string.Empty;
            GraceStatusText.Text = string.Empty;
            NoticeHost.Visibility = Visibility.Collapsed;
            GraceStatus.Visibility = Visibility.Collapsed;
            ClipboardStatus.Visibility = Visibility.Collapsed;
        }
        UpdateResponsiveShell();
    }

    private void RefreshShellNavigation(bool force = false)
    {
        if (Nav is null)
            return;
        var page = ContentFrame.Content switch
        {
            VaultPage => "vault", SendPage => "send", GeneratorPage => "gen",
            SecurityReportPage => "reports", SettingsPage or DevicesPage or ImportExportPage or EmergencyAccessPage => "settings",
            _ => string.Empty,
        };
        var changed = page != _shellPage;
        ObserveVaultViewModel(IsVaultUnlocked()
            ? (ContentFrame.Content as VaultPage)?.ViewModel
            : null);
        if (ContentFrame.Content is VaultPage currentVault) _cachedVaultPage = currentVault;
        if (ContentFrame.Content is SendPage currentSend && !ReferenceEquals(currentSend, _cachedSendPage))
        {
            if (_cachedSendPage is not null)
            {
                _cachedSendPage.ViewModel.Items.CollectionChanged -= OnSendItemsChanged;
                _cachedSendPage.ContextChanged -= OnSendContextChanged;
            }
            _cachedSendPage = currentSend;
            currentSend.ViewModel.Items.CollectionChanged += OnSendItemsChanged;
            currentSend.ContextChanged += OnSendContextChanged;
        }
        if (changed && (_shellPage == "send" || page == "send"))
        {
            _searchUpdating = true;
            _cachedVaultPage?.ApplySearch(string.Empty);
            _cachedSendPage?.ApplySearch(string.Empty);
            TitleSearch.Text = string.Empty;
            _searchUpdating = false;
        }
        if (changed || force)
        {
            _shellPage = page;
            _historyCopyButtons.Clear();
            Nav.MenuItems.Clear();
            Nav.FooterMenuItems.Clear();
            _folderGroupHeader = null;
            _folderAddButton = null;
            switch (page)
            {
                case "vault":
                    foreach (var item in new NavigationViewItem[] { AllItemsNavItem, FavoritesNavItem, TotpNavItem })
                        Nav.MenuItems.Add(item);
                    AddVaultGroup(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C7802D8ADAED72E), FoldersNavItem);
                    AddVaultGroup(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4D6A4BC105DD3F07), OrganizationsNavItem);
                    AddVaultGroup(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text528E5174C2D4E5C8), CollectionsNavItem);
                    Nav.FooterMenuItems.Add(ArchiveNavItem);
                    Nav.FooterMenuItems.Add(TrashNavItem);
                    break;
                case "gen":
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD7A101216E4AD4B1), "gen:password", "\uE8D7");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCB1B19977701FF9B), "gen:passphrase", "\uE8E9");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1A3F0617D6DE8E52), "gen:username", "\uE77B");
                    AddGeneratorHistory();
                    if (changed) _contextTag = "gen:password";
                    break;
                case "send":
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text08F06E58676001CE), "send:all", "\uE724");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDD81861811E57BF7), "send:text", "\uE8A5");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text39932F24FE11A6BA), "send:file", "\uE8A5");
                    AddContextHeader(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6E684586884EBEE6));
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text20F27E9DD9D032DA), "send:receive", "\uE71B");
                    if (changed) _contextTag = "send:all";
                    break;
                case "reports":
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFEA405F9B01D1416), "reports:all", "\uEA18");
                    AddContextHeader(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB7E3E715F18B9BAC));
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3E20E2572330B2C0), "reports:breach", "\uE783");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB82FC779B955EC5E), "reports:reuse", "\uE8EE");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEAC3BB39F8F0C574), "reports:weak", "\uE7BA");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text35CB23E74DACE235), "reports:http", "\uE785");
                    AddContextHeader(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text92D5ADC36C2DE722));
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4BB9578CA0B97C1F), "reports:done", "\uE73E");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC9BB70BCAAA3BEE1), "reports:ignored", "\uE890");
                    if (changed) _contextTag = "reports:all";
                    break;
                case "settings":
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text061246BD31E084A7), "settings:account", "\uE77B");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAFB63A620BDCFF15), "settings:security", "\uE72E");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3F5E6929157C5E75), "settings:ssh", "\uE756");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE1506406A5BD8F02), "settings:devices", "\uE772");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD99C4E425CC754DE), "settings:import", "\uE8AB");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0371F8EADCAA92A3), "settings:appearance", "\uE790");
                    AddContextItem(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text52D25A9E30BA94F1), "settings:about", "\uE946");
                    if (changed) _contextTag = "settings:account";
                    break;
            }
            if (changed && _uiSettings.AnimationsEnabled)
                AnimateContentEntrance();
        }
        if (ContentFrame.Content is VaultPage vault)
        {
            _contextTag = vault.ViewModel.SelectedFilterTag ?? "vault:allitems";
            foreach (var group in new[] { FoldersNavItem, OrganizationsNavItem, CollectionsNavItem })
                group.Visibility = group.MenuItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchUpdating = true;
            TitleSearch.Text = vault.ViewModel.SearchText;
            _searchUpdating = false;
        }
        else if (ContentFrame.Content is GeneratorPage)
            _contextTag = global::App.App.Services.GetRequiredService<GeneratorViewModel>().Mode switch
            {
                GeneratorMode.Passphrase => "gen:passphrase", GeneratorMode.Username => "gen:username", _ => "gen:password",
            };
        else if (ContentFrame.Content is SecurityReportPage reports)
            _contextTag = "reports:" + reports.ViewModel.Category;
        else if (ContentFrame.Content is SettingsPage settings)
            _contextTag = "settings:" + settings.CurrentSection;
        else if (ContentFrame.Content is SendPage send)
        {
            _contextTag = send.CurrentFilterTag;
            _searchUpdating = true;
            TitleSearch.Text = send.ViewModel.SearchQuery;
            _searchUpdating = false;
        }
        Nav.SelectedItem = FindNavigationItem(_contextTag);
        VaultTab.IsChecked = page == "vault";
        GeneratorTab.IsChecked = page == "gen";
        SendTab.IsChecked = page == "send";
        ReportsTab.IsChecked = page == "reports";
        TitleSearch.PlaceholderText = page == "send" && _cachedSendPage?.IsReceiveView != true ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text53A3782E240E2CD1) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAE9EE7FE108525EE);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TitleSearch, TitleSearch.PlaceholderText);
        UpdateNavigationCounts();
        UpdateResponsiveShell();
    }

    private NavigationViewItem AddContextItem(string label, string tag, string glyph)
    {
        var item = new NavigationViewItem { Content = label, Tag = tag, Icon = new FontIcon { Glyph = glyph, FontSize = 16 }, MinHeight = 34, Height = 34, Margin = new Thickness(0, 1, 0, 1) };
        ToolTipService.SetToolTip(item, label);
        Nav.MenuItems.Add(item);
        return item;
    }

    private void AddContextHeader(string label) => Nav.MenuItems.Add(new NavigationViewItemHeader { Content = label });

    private void AddVaultGroup(string label, NavigationViewItem group)
    {
        if (group == FoldersNavItem)
            AddFolderGroupHeader(label);
        else if (group.MenuItems.Count > 0)
            AddContextHeader(label);
        foreach (var child in group.MenuItems.OfType<NavigationViewItem>())
        {
            var folderName = child.Content as string ?? string.Empty;
            var tag = child.Tag as string ?? string.Empty;
            var item = AddContextItem(folderName, tag, (child.Icon as FontIcon)?.Glyph ?? "\uE8B7");
            if (group == FoldersNavItem && tag.StartsWith("vault:folder:", StringComparison.Ordinal))
                item.ContextFlyout = CreateFolderContextMenu(tag["vault:folder:".Length..], folderName);
        }
    }

    private void AddFolderGroupHeader(string label)
    {
        var content = new Grid { MinHeight = 24, ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            LineHeight = 24, VerticalAlignment = VerticalAlignment.Center,
        });
        var add = new Button
        {
            Width = 24, Height = 24, MinWidth = 24, MinHeight = 24,
            Padding = new Thickness(4), Style = (Style)Application.Current.Resources["IconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE710", FontSize = 14 },
        };
        ToolTipService.SetToolTip(add, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text84244ABC71DE03AC));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text84244ABC71DE03AC));
        _folderAddButton = add;
        UpdateFolderActionState();
        add.Click += async (_, _) => await InvokeFolderActionAsync("create");
        Grid.SetColumn(add, 1);
        content.Children.Add(add);
        _folderGroupHeader = new NavigationViewItem
        {
            Content = content, Tag = "shell:folder-header", SelectsOnInvoked = false, IsTabStop = false,
            Style = (Style)Application.Current.Resources["WardenNavigationHeaderStyle"],
        };
        Nav.MenuItems.Add(_folderGroupHeader);
    }

    private MenuFlyout CreateFolderContextMenu(string folderId, string folderName)
    {
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0D0CBAC2EEE54113), Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += async (_, _) => await InvokeFolderActionAsync("rename", folderId, folderName);
        var delete = new MenuFlyoutItem { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text780DFC4C114297D3), Icon = new FontIcon { Glyph = "\uE74D" } };
        delete.Click += async (_, _) => await InvokeFolderActionAsync("delete", folderId, folderName);
        menu.Items.Add(rename);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(delete);
        return menu;
    }

    private async Task InvokeFolderActionAsync(string action, string? folderId = null, string? folderName = null)
    {
        if (!IsVaultUnlocked() || !Nav.IsEnabled || ContentFrame.Content is not VaultPage vault)
            return;
        if (!vault.ViewModel.CanCancelEditor)
        {
            UpdateFolderActionState();
            ShowNotice(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text442C52CE4ED69957));
            return;
        }
        switch (action)
        {
            case "create":
                await vault.CreateFolderAsync();
                break;
            case "rename" when folderId is not null && folderName is not null:
                await vault.RenameFolderAsync(folderId, folderName);
                break;
            case "delete" when folderId is not null && folderName is not null:
                await vault.DeleteFolderAsync(folderId, folderName);
                break;
        }
    }

    private void AddGeneratorHistory()
    {
        if (ShellRoot.ActualWidth < 1200 && !Nav.IsPaneOpen)
            return;
        AddContextHeader(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0A6DB89EB940B391));
        foreach (var history in global::App.App.Services.GetRequiredService<GeneratorViewModel>().History.Take(5))
        {
            var item = new NavigationViewItem { SelectsOnInvoked = false, MinHeight = 32, Padding = new Thickness(14, 0, 8, 0) };
            var grid = new Grid { Width = 186, ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = history.Value, FontFamily = (FontFamily)Application.Current.Resources["MonospaceFontFamily"], FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var time = new TextBlock { Text = history.CreatedAt.ToLocalTime().ToString("HH:mm"), Style = (Style)Application.Current.Resources["WardenCountTextStyle"] };
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
            var copy = new Button { Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 }, Style = (Style)Application.Current.Resources["IconButtonStyle"], MinHeight = 28, MinWidth = 28, Padding = new Thickness(4) };
            Grid.SetColumn(copy, 2);
            _historyCopyButtons[copy] = 0;
            copy.Click += (_, _) =>
            {
                if (IsVaultUnlocked())
                {
                    global::App.App.Services.GetRequiredService<GeneratorViewModel>().CopyHistoryItemCommand.Execute(history);
                    _clipboardStatus?.SetLabel(history.Mode == GeneratorMode.Username ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1A3F0617D6DE8E52) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA621AB606DB2A11F));
                    _historyCopyButtons[copy] = _clipboardStatus?.WriteVersion ?? 0;
                    copy.Content = new FontIcon { Glyph = "\uE73E", FontSize = 14 };
                }
            };
            ToolTipService.SetToolTip(copy, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text63D90D977348AB1F));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDDDF686AD0853981));
            grid.Children.Add(copy);
            item.Content = grid;
            ToolTipService.SetToolTip(item, history.CreatedAtText);
            Nav.MenuItems.Add(item);
        }
    }

    private void UpdateNavigationCounts()
    {
        if (!IsVaultUnlocked())
            return;
        var report = global::App.App.Services.GetRequiredService<SecurityReportViewModel>();
        ReportIssueCount.Text = report.OverviewIssueCount > 0 ? report.OverviewIssueCount.ToString() : string.Empty;
        if (_shellPage == "vault")
        {
            var items = global::App.App.Services.GetRequiredService<IVaultUiService>().GetItems();
            SetNavigationCount(AllItemsNavItem, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text80A851B1DF45DC50), items.Count(i => !i.IsDeleted && !i.IsArchived));
            SetNavigationCount(FavoritesNavItem, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text60A53514EB9228A2), items.Count(i => !i.IsDeleted && !i.IsArchived && i.Favorite));
            SetNavigationCount(TotpNavItem, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBB015C60BAFC8A96), items.Count(i => !i.IsDeleted && !i.IsArchived && i.HasTotp));
            SetNavigationCount(ArchiveNavItem, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5292AB1A36C56A6B), items.Count(i => !i.IsDeleted && i.IsArchived));
            SetNavigationCount(TrashNavItem, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBA35DC23B245E61E), items.Count(i => i.IsDeleted));
            foreach (var filter in global::App.App.Services.GetRequiredService<IVaultUiService>().GetFilters())
            {
                var tag = filter.Kind switch
                {
                    FilterKind.Folder => "vault:folder:" + filter.FolderId,
                    FilterKind.Organization => "vault:organization:" + filter.OrganizationId,
                    FilterKind.Collection => "vault:collection:" + filter.CollectionId,
                    _ => string.Empty,
                };
                if (!string.IsNullOrEmpty(tag)) SetContextCount(tag, filter.Label, filter.Count);
            }
        }
        else if (_shellPage == "send" && ContentFrame.Content is SendPage send)
        {
            SetContextCount("send:all", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text08F06E58676001CE), send.ViewModel.Items.Count);
            SetContextCount("send:text", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDD81861811E57BF7), send.ViewModel.Items.Count(i => i.Type == SendType.Text));
            SetContextCount("send:file", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text39932F24FE11A6BA), send.ViewModel.Items.Count(i => i.Type == SendType.File));
        }
        else if (_shellPage == "reports")
        {
            SetContextCount("reports:breach", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3E20E2572330B2C0), report.ShowExposedCount
                ? report.GetCategoryIssueCount("breach").ToString() : "—");
            SetContextCount("reports:reuse", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB82FC779B955EC5E), report.GetCategoryIssueCount("reuse"));
            SetContextCount("reports:weak", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEAC3BB39F8F0C574), report.GetCategoryIssueCount("weak"));
            SetContextCount("reports:http", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text35CB23E74DACE235), report.GetCategoryIssueCount("http"));
            SetContextCount("reports:done", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4BB9578CA0B97C1F), report.ResolvedCount);
            SetContextCount("reports:ignored", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC9BB70BCAAA3BEE1), report.GetCategoryIssueCount("ignored"));
        }
        else if (_shellPage == "settings" && FindNavigationItem("settings:devices") is { } devices)
        {
            var count = global::App.App.Services.GetRequiredService<DeviceApprovalWindowService>().PendingCount;
            if (count > 0) SetNavigationCount(devices, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE1506406A5BD8F02), count);
            else devices.Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE1506406A5BD8F02);
        }
    }

    private void SetContextCount(string tag, string label, int count)
    {
        if (FindNavigationItem(tag) is { } item)
            SetNavigationCount(item, label, count);
    }

    private void SetContextCount(string tag, string label, string count)
    {
        if (FindNavigationItem(tag) is { } item)
            SetNavigationCount(item, label, count);
    }

    private static void SetNavigationCount(NavigationViewItem item, string label, int count) =>
        SetNavigationCount(item, label, count.ToString());

    private static void SetNavigationCount(NavigationViewItem item, string label, string count)
    {
        item.MinHeight = item.Height = 34;
        item.Margin = new Thickness(0, 1, 0, 1);
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var grid = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
        var countText = new TextBlock { Text = count, Style = (Style)Application.Current.Resources["WardenCountTextStyle"] };
        Grid.SetColumn(countText, 1);
        grid.Children.Add(countText);
        item.Content = grid;
        ToolTipService.SetToolTip(item, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, count == "—" ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text9E6EA02EF40C0B9A,label) : $"{label} {count}");
    }

    private async Task<bool> NavigateContextTagAsync(string tag)
    {
        if (tag.StartsWith("settings:", StringComparison.Ordinal))
        {
            if (ContentFrame.Content is not SettingsPage)
                _nav.GoToSettings();
            (ContentFrame.Content as SettingsPage)?.SelectSection(tag[9..]);
        }
        else if (tag.StartsWith("gen:", StringComparison.Ordinal))
        {
            if (ContentFrame.Content is not GeneratorPage)
                _nav.GoToGenerator();
            (ContentFrame.Content as GeneratorPage)?.SelectMode(tag[4..]);
        }
        else if (tag.StartsWith("reports:", StringComparison.Ordinal))
        {
            if (ContentFrame.Content is not SecurityReportPage)
                _nav.GoToSecurityReport();
            (ContentFrame.Content as SecurityReportPage)?.SelectCategory(tag[8..]);
        }
        else
            return false;
        _contextTag = tag;
        Nav.SelectedItem = FindNavigationItem(tag);
        if (Nav.DisplayMode != NavigationViewDisplayMode.Expanded)
            Nav.IsPaneOpen = false;
        if (_uiSettings.AnimationsEnabled)
            AnimateContentEntrance();
        await Task.CompletedTask;
        return true;
    }

    private async void OnTopTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            await NavigateByTagAsync(tag, preserveDraft: true);
        RefreshShellNavigation();
    }

    private async void OnShellMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            await NavigateByTagAsync(tag);
        RefreshShellNavigation();
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e) => await NavigateByTagAsync("settings:account");
    private async void OnLockClick(object sender, RoutedEventArgs e) => await NavigateByTagAsync("lock");

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        var index = ShellRoot.ActualTheme == ElementTheme.Dark ? 1 : 2;
        AppPreferences.Current.ThemeIndex = index;
        AppPreferences.Save();
        ThemeManager.Apply(index);
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon() => ThemeIcon.Glyph = ShellRoot.ActualTheme == ElementTheme.Dark ? "\uE706" : "\uE708";
    private void OnPaneToggleClick(object sender, RoutedEventArgs e) => Nav.IsPaneOpen = !Nav.IsPaneOpen;
    private void OnNewVaultClick(SplitButton sender, SplitButtonClickEventArgs args) => (ContentFrame.Content as VaultPage)?.BeginNewItem();
    private void OnNewSendClick(SplitButton sender, SplitButtonClickEventArgs args) => (ContentFrame.Content as SendPage)?.BeginCreate();
    private void OnNewVaultMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<VaultItemKind>(tag, out var kind))
            (ContentFrame.Content as VaultPage)?.BeginNewItem(kind);
    }
    private void OnNewSendMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SendType>(tag, out var kind))
            (ContentFrame.Content as SendPage)?.BeginCreate(kind);
    }
    private void OnCompactNewClick(object sender, RoutedEventArgs e)
    {
        var flyout = _shellPage == "send" ? NewSendButton.Flyout : NewVaultButton.Flyout;
        flyout?.ShowAt(CompactNewButton);
    }
    private async void OnRecheckClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is not SecurityReportPage report)
            return;
        RecheckButton.IsEnabled = false;
        RecheckLabel.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6B72C3D6855CB87D);
        try { await report.ViewModel.RefreshReportsAsync(); }
        finally { RecheckButton.IsEnabled = true; RecheckLabel.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC25FB86B1E96E063); }
    }

    public void FocusVaultSearch()
    {
        if (!IsVaultUnlocked() || HasOpenPopup()) return;
        _compactSearchOpen = true;
        UpdateResponsiveShell();
        TitleSearch.Focus(FocusState.Programmatic);
        TitleSearch.SelectAll();
    }
    private void OnCompactSearchClick(object sender, RoutedEventArgs e) => FocusVaultSearch();
    private bool HasOpenPopup() => ShellRoot.XamlRoot is not null
        && VisualTreeHelper.GetOpenPopupsForXamlRoot(ShellRoot.XamlRoot).Any(popup => popup.IsOpen);
    private async void OnTitleSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (ClearSearchButton is not null)
        {
            ClearSearchButton.Visibility = string.IsNullOrEmpty(TitleSearch.Text) ? Visibility.Collapsed : Visibility.Visible;
            TitleSearchIcon.Visibility = string.IsNullOrEmpty(TitleSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_searchUpdating || !IsVaultUnlocked()) return;
        var query = TitleSearch.Text;
        if (ContentFrame.Content is SendPage send && !send.IsReceiveView)
            send.ApplySearch(query);
        else
        {
            if (ContentFrame.Content is not VaultPage)
                await NavigateByTagAsync("vault");
            (ContentFrame.Content as VaultPage)?.ApplySearch(query);
        }
    }
    private void OnTitleSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            TitleSearch.Text = string.Empty;
            _compactSearchOpen = false;
            UpdateResponsiveShell();
            e.Handled = true;
        }
        else if (ContentFrame.Content is VaultPage vault)
        {
            if (e.Key is VirtualKey.Up or VirtualKey.Down)
                e.Handled = vault.MoveSearchSelection(e.Key == VirtualKey.Down ? 1 : -1);
            else if (e.Key == VirtualKey.Enter)
            {
                vault.AcceptSearchResult();
                e.Handled = true;
            }
        }
        else if (ContentFrame.Content is SendPage send)
        {
            if (e.Key is VirtualKey.Up or VirtualKey.Down)
            {
                send.MoveSearchSelection(e.Key == VirtualKey.Down ? 1 : -1);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Enter)
            {
                send.AcceptSearchResult();
                e.Handled = true;
            }
        }
    }
    private void OnTitleSearchLostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TitleSearch.Text))
        {
            _compactSearchOpen = false;
            UpdateResponsiveShell();
        }
    }
    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        TitleSearch.Text = string.Empty;
        _compactSearchOpen = false;
        UpdateResponsiveShell();
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e) => UpdateResponsiveShell();
    private void OnNavDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args) => UpdateResponsiveShell();
    private void UpdateResponsiveShell()
    {
        if (Nav is null || AppNameText is null || TitleSearchHost is null) return;
        var width = ShellRoot.ActualWidth;
        var unlocked = !_isLoginWindow && Nav.Visibility == Visibility.Visible;
        var wasWide = _lastShellWidth >= 1200;
        _lastShellWidth = width;
        if (unlocked && Nav.IsLoaded)
        {
            if (width >= 1200 && !Nav.IsPaneOpen)
                Nav.IsPaneOpen = true;
            else if (width is > 0 and < 1200 && wasWide)
                Nav.IsPaneOpen = false;
        }
        var compact = width < 1200 && !Nav.IsPaneOpen;
        if (_folderGroupHeader is not null)
            _folderGroupHeader.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        var narrow = width < 800;
        if (unlocked && width is > 0 and < 640)
            DispatcherQueue.TryEnqueue(() => ResizeClientDip(640, Math.Max(480, ShellRoot.ActualHeight)));
        if (_shellPage == "gen" && _historyExpanded != !compact)
        {
            _historyExpanded = !compact;
            DispatcherQueue.TryEnqueue(() => RefreshShellNavigation(force: true));
        }
        AppNameText.Visibility = width < 1000 && unlocked ? Visibility.Collapsed : Visibility.Visible;
        BrandPanel.Margin = new Thickness(0, 0, width < 1000 ? 4 : 16, 0);
        TopTabs.Visibility = unlocked && !(narrow && _compactSearchOpen) ? Visibility.Visible : Visibility.Collapsed;
        TitleSearchHost.Visibility = unlocked && (!narrow || _compactSearchOpen) ? Visibility.Visible : Visibility.Collapsed;
        CompactSearchButton.Visibility = unlocked && narrow && !_compactSearchOpen ? Visibility.Visible : Visibility.Collapsed;
        var captionWidth = RightPaddingColumn.ActualWidth > 0
            ? RightPaddingColumn.ActualWidth : RightPaddingColumn.Width.Value;
        SetCompactTopTabs(unlocked && TitleBarLayoutPolicy.NeedCompactTabs(
            width, _expandedTopTabsWidth,
            BrandPanel.ActualWidth + BrandPanel.Margin.Left + BrandPanel.Margin.Right,
            TitleActions.ActualWidth + TitleActions.Margin.Left + TitleActions.Margin.Right,
            captionWidth, AppTitleBar.Padding.Left + AppTitleBar.Padding.Right + AppTitleBar.ColumnSpacing * 4,
            !narrow ? TitleSearch.MinWidth + 20 : 0));
        var centerSearch = false;
        if (unlocked && width >= 1280 && TopTabs.ActualWidth > 0 && TitleActions.ActualWidth > 0)
        {
            var tabs = TopTabs.TransformToVisual(AppTitleBar).TransformBounds(new Rect(0, 0, TopTabs.ActualWidth, TopTabs.ActualHeight));
            var actions = TitleActions.TransformToVisual(AppTitleBar).TransformBounds(new Rect(0, 0, TitleActions.ActualWidth, TitleActions.ActualHeight));
            centerSearch = TitleBarLayoutPolicy.CanCenterSearch(width, tabs.Right, actions.Left);
        }
        Grid.SetColumn(TitleSearchHost, centerSearch ? 0 : narrow ? 1 : 2);
        Grid.SetColumnSpan(TitleSearchHost, centerSearch ? 5 : narrow ? 2 : 1);
        TitleSearchHost.HorizontalAlignment = centerSearch ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        TitleSearchHost.Width = centerSearch ? 360 : double.NaN;
        TitleSearchHost.Margin = centerSearch ? new Thickness(0) : new Thickness(12, 0, 8, 0);
        TitleSearchHost.RenderTransform = new TranslateTransform { X = centerSearch ? -8 : 0 };
        TitleSearchHost.MaxWidth = narrow ? double.PositiveInfinity : 360;
        Nav.IsPaneToggleButtonVisible = width < 1200;
        PaneToggleButton.Visibility = Visibility.Collapsed;
        UpdateCompactPaneAction(compact);
        PaneActions.Margin = new Thickness(compact ? 4 : 8, 4, compact ? 4 : 8, 12);
        NewVaultButton.Visibility = !compact && _shellPage == "vault" ? Visibility.Visible : Visibility.Collapsed;
        NewSendButton.Visibility = !compact && _shellPage == "send" ? Visibility.Visible : Visibility.Collapsed;
        CompactNewButton.Visibility = compact && _shellPage is "vault" or "send" ? Visibility.Visible : Visibility.Collapsed;
        RecheckButton.Visibility = _shellPage == "reports" ? Visibility.Visible : Visibility.Collapsed;
        RecheckLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RecheckButton.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        RecheckButton.Width = compact ? 40 : double.NaN;
        SettingsAccountHeader.Visibility = _shellPage == "settings" && !compact ? Visibility.Visible : Visibility.Collapsed;
        AccountServerText.Visibility = width >= 1000 ? Visibility.Visible : Visibility.Collapsed;
        SshStatusLabel.Visibility = !narrow ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusBar();
        DispatcherQueue.TryEnqueue(UpdateTitleBarRegions);
    }

    private void UpdateCompactPaneAction(bool compact)
    {
        var tag = compact ? _shellPage switch
        {
            "vault" or "send" => "shell:new",
            "reports" => "shell:recheck",
            _ => string.Empty,
        } : string.Empty;
        if (_compactActionItem is not null && (string.IsNullOrEmpty(tag) || (string?)_compactActionItem.Tag != tag))
        {
            Nav.MenuItems.Remove(_compactActionItem);
            _compactActionItem = null;
        }
        if (string.IsNullOrEmpty(tag)) return;
        if (_compactActionItem is null)
        {
            _compactActionItem = new NavigationViewItem
            {
                Tag = tag, SelectsOnInvoked = false,
                Content = tag == "shell:new" ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text50EF2F4CF6A46924) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC25FB86B1E96E063),
                Icon = new FontIcon { Glyph = tag == "shell:new" ? "\uE710" : "\uE72C", FontSize = 16 },
                MinHeight = 32, Height = 32,
            };
            if (tag == "shell:new") _compactActionItem.Style = (Style)Application.Current.Resources["WardenCompactNewStyle"];
            ToolTipService.SetToolTip(_compactActionItem, _compactActionItem.Content);
        }
        if (!Nav.MenuItems.Contains(_compactActionItem)) Nav.MenuItems.Insert(0, _compactActionItem);
    }

    private void SetCompactTopTabs(bool compact)
    {
        if (_compactTopTabs == compact || _expandedTopTabContents.Count == 0)
            return;
        _compactTopTabs = compact;
        foreach (var (tab, glyph) in new[]
        {
            (VaultTab, "\uE72E"), (GeneratorTab, "\uE8B1"),
            (SendTab, "\uE724"), (ReportsTab, "\uE9D9"),
        })
        {
            if (compact)
            {
                var icon = new FontIcon { Glyph = glyph, FontSize = 16 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                    icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                tab.Content = icon;
                tab.MinWidth = 40;
                tab.Width = 40;
                tab.Padding = new Thickness(8, 0, 8, 0);
            }
            else
            {
                tab.Content = _expandedTopTabContents[tab];
                tab.ClearValue(FrameworkElement.MinWidthProperty);
                tab.ClearValue(FrameworkElement.WidthProperty);
                tab.ClearValue(Control.PaddingProperty);
            }
        }
    }

    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar.XamlRoot is null) return;
        UpdateSelectedTabIndicator();
        var scale = AppTitleBar.XamlRoot.RasterizationScale;
        var regions = new List<RectInt32>();
        foreach (var element in new FrameworkElement[] { TopTabs, TitleSearchHost, TitleActions })
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0) continue;
            var bounds = element.TransformToVisual(ShellRoot).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            regions.Add(new RectInt32((int)Math.Floor(bounds.X * scale), (int)Math.Floor(bounds.Y * scale), (int)Math.Ceiling(bounds.Width * scale), (int)Math.Ceiling(bounds.Height * scale)));
        }
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, regions.ToArray());
    }

    private void UpdateSelectedTabIndicator()
    {
        var selected = _shellPage switch
        {
            "vault" => VaultTab, "gen" => GeneratorTab, "send" => SendTab, "reports" => ReportsTab, _ => null,
        };
        if (selected is null || TopTabs.Visibility != Visibility.Visible)
        {
            SelectedTabIndicator.Visibility = Visibility.Collapsed;
            return;
        }
        var target = selected.TransformToVisual(TopTabs).TransformPoint(new Point(0, 0)).X + (selected.ActualWidth - 16) / 2;
        var wasVisible = SelectedTabIndicator.Visibility == Visibility.Visible;
        SelectedTabIndicator.Visibility = Visibility.Visible;
        if (Math.Abs(target - _tabIndicatorTarget) < 0.5) return;
        _tabIndicatorTarget = target;
        var start = SelectedTabTranslation.X;
        _tabIndicatorAnimation?.Stop();
        if (!_uiSettings.AnimationsEnabled || !wasVisible)
        {
            SelectedTabTranslation.X = target;
            return;
        }
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { Value = start, KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero) });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            Value = target, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
            KeySpline = new KeySpline { ControlPoint1 = new Point(0.3, 0), ControlPoint2 = new Point(0, 1) },
        });
        Storyboard.SetTarget(animation, SelectedTabTranslation);
        Storyboard.SetTargetProperty(animation, "X");
        _tabIndicatorAnimation = new Storyboard();
        _tabIndicatorAnimation.Children.Add(animation);
        _tabIndicatorAnimation.Begin();
    }

    private void AnimateContentEntrance()
    {
        var transform = new TranslateTransform();
        ContentFrame.RenderTransform = transform;
        var storyboard = new Storyboard();
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(333), EasingFunction = easing };
        Storyboard.SetTarget(opacity, ContentFrame);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        var move = new DoubleAnimation { From = 16, To = 0, Duration = TimeSpan.FromMilliseconds(333), EasingFunction = easing };
        Storyboard.SetTarget(move, transform);
        Storyboard.SetTargetProperty(move, "Y");
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(move);
        storyboard.Begin();
    }

    private void OnShellStatusChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateStatusBar);
    private void UpdateStatusBar()
    {
        if (ClipboardStatus is null || _isLoginWindow) return;
        if (SshStatusLabel is not null)
            SshStatusLabel.Text = global::App.App.Services.GetRequiredService<ISshAgentHost>().IsRunning
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text40FE762563572B54)
                : AppPreferences.Current.SshAgentEnabled ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8BFB4AC34B2A0735) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8237D3DDFE128CAB);
        var pendingRequests = global::App.App.Services.GetRequiredService<DeviceApprovalWindowService>().PendingCount;
        if (pendingRequests != _lastPendingRequestCount)
        {
            _lastPendingRequestCount = pendingRequests;
            UpdateNavigationCounts();
        }
        if (_noticeUntil < DateTimeOffset.UtcNow) NoticeHost.Visibility = Visibility.Collapsed;
        if (!_syncing && _lastSyncAt is { } synced)
        {
            var minutes = (int)(DateTimeOffset.UtcNow - synced).TotalMinutes;
            SyncStatusText.Text = minutes == 0 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD91CF2173E0AD1CB) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text61E5D07D00850090,minutes);
        }
        var grace = _sensitiveActions?.GraceRemaining ?? TimeSpan.Zero;
        GraceStatus.Visibility = grace > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        GraceStatusText.Text = ShellRoot.ActualWidth < 1000 ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextB7E7A97A8DCFBCA6,grace.Minutes,grace.Seconds) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text384966EE5AE94371,grace.Minutes,grace.Seconds);
        var clip = _clipboardStatus;
        foreach (var (button, version) in _historyCopyButtons)
        {
            if (button.Content is FontIcon icon)
                icon.Glyph = version > 0 && clip?.HasContent == true && clip.WriteVersion == version ? "\uE73E" : "\uE8C8";
        }
        if (clip?.HasContent == true)
        {
            ClipboardStatus.Visibility = Visibility.Visible;
            ClearClipboardButton.Visibility = Visibility.Visible;
            var remaining = clip.ClearAt is { } clearAt ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text2A26888143C316A2,Math.Max(0, (int)Math.Ceiling((clearAt - DateTimeOffset.UtcNow).TotalSeconds))) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2744A7F44B3E4C5E);
            var history = AppPreferences.Current.ExcludeClipboardHistory && ShellRoot.ActualWidth >= 1280 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA8D10AFC14137EF2) : string.Empty;
            ClipboardStatusText.Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text6FD19F7D346EC57E,clip.Label,remaining,history);
        }
        else if (clip?.LastClearedAt is { } cleared && DateTimeOffset.UtcNow - cleared < TimeSpan.FromSeconds(3))
        {
            ClipboardStatus.Visibility = Visibility.Visible;
            ClipboardStatusText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9F5F52DF1D67E6C9);
            ClearClipboardButton.Visibility = Visibility.Collapsed;
        }
        else ClipboardStatus.Visibility = Visibility.Collapsed;
    }

    private async void OnSyncClick(object sender, RoutedEventArgs e)
    {
        if (_syncing || !IsVaultUnlocked()) return;
        _syncing = true;
        SyncStatusText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text954EC984CD4F49D1);
        SyncStatusButton.IsEnabled = false;
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var lease = session.CaptureLease();
        try
        {
            await global::App.App.Services.GetRequiredService<IVaultUiService>().SyncAsync(lease.CancellationToken);
            if (!session.IsUnlocked(lease)) return;
            _lastSyncAt = DateTimeOffset.UtcNow;
            RefreshVaultSnapshot();
            RefreshSendList();
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (session.IsUnlocked(lease))
            {
                _lastSyncAt = null;
                SyncStatusText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text88B4C02248ADA065);
                ShowNotice(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF52CF48CAA3117DF));
            }
            CrashLogger.WriteRedacted("ShellSync", ex);
        }
        finally { _syncing = false; SyncStatusButton.IsEnabled = true; UpdateStatusBar(); }
    }

    private void OnClearClipboardClick(object sender, RoutedEventArgs e) => global::App.App.Services.GetRequiredService<IClipboardService>().ClearSecretsNow();
    private void OnEndGraceClick(object sender, RoutedEventArgs e) => _sensitiveActions?.EndGracePeriod();
    private void OnDismissNoticeClick(object sender, RoutedEventArgs e) => NoticeHost.Visibility = Visibility.Collapsed;
    public void ShowNotice(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsVaultUnlocked()) return;
            NoticeText.Text = message;
            NoticeHost.Visibility = Visibility.Visible;
            _noticeUntil = DateTimeOffset.UtcNow.AddSeconds(5);
        });
    }

    public void MinimizeAfterCopy()
    {
        if (IsVaultUnlocked())
            (AppWindow.Presenter as OverlappedPresenter)?.Minimize();
    }

    public async Task<bool> UnlockForDeviceApprovalAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested || LoginFrame.Content is not LoginPage login || !login.ViewModel.ShowHelloUnlock)
            return false;
        await login.ViewModel.UnlockWithHelloCommand.ExecuteAsync(null);
        return !ct.IsCancellationRequested && IsVaultUnlocked();
    }
}
