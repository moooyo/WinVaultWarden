using App.Controls;
using App.ViewModels.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views
{
    public sealed partial class VaultPage
    {
        internal bool NativeReviewLoginEditorStructureValid
        {
            get
            {
                DetailCard? loginCard = null;
                for (DependencyObject? node = LoginPasswordEditor; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                    if (node is DetailCard card) { loginCard = card; break; }
                return CipherEditorNameBox.ActualWidth >= 120 && LoginPasswordEditor.ActualWidth >= 100
                    && LoginPasswordEditor.ActualHeight >= 32 && TotpSecretEditor.Parent is Grid { ActualHeight: <= 56 }
                    && !string.IsNullOrEmpty(TotpSecretEditor.PlaceholderText)
                    && loginCard is not null && string.IsNullOrWhiteSpace(loginCard.Title)
                    && loginCard.FindName("TitleText") is FrameworkElement { Visibility: Visibility.Collapsed }
                    && !Descendants<Expander>(CipherEditorPanel).Any(expander => expander.Visibility == Visibility.Visible);
            }
        }
        internal void NativeReviewBringUriMatchIntoView(LoginUriEditorDraft uri) => NativeReviewUriMatchButton(uri).StartBringIntoView();
        private Button NativeReviewUriMatchButton(LoginUriEditorDraft uri) => Descendants<Button>(CipherEditorPanel)
            .First(button => ReferenceEquals(button.Tag, uri) && button.Flyout is MenuFlyout);
        internal void NativeReviewOpenUriMatchMenu(LoginUriEditorDraft uri)
        {
            var button = NativeReviewUriMatchButton(uri);
            var menu = (MenuFlyout)button.Flyout;
            menu.AreOpenCloseAnimationsEnabled = false;
            menu.ShowAt(button);
        }
        internal int NativeReviewUriRuleCount => _uriMatchFlyout?.Items.OfType<ToggleMenuFlyoutItem>()
            .Count(item => item.Tag is UriMatchSelection) ?? 0;
        internal bool NativeReviewUriMatchMenuReady => NativeReviewUriRuleCount == 7
            && _uriMatchFlyout!.Items.OfType<ToggleMenuFlyoutItem>().All(item => item.IsLoaded);
        internal bool NativeReviewUriMatchMenuClosed => _uriMatchFlyout is null;
        internal bool NativeReviewUnknownUriRuleShown(LoginUriEditorDraft uri) => _uriMatchFlyout?.Items
            .OfType<MenuFlyoutItem>().Any(item => !item.IsEnabled && item.Text == uri.MatchSummary) == true;
        internal ToggleMenuFlyoutItem NativeReviewUriRuleItem(int index) => _uriMatchFlyout!.Items.OfType<ToggleMenuFlyoutItem>()
            .First(item => item.Tag is UriMatchSelection selection && selection.MatchIndex == index);
        internal void NativeReviewApplyUriRule(ToggleMenuFlyoutItem item) => OnUriMatchItemClick(item, new RoutedEventArgs());
        internal void NativeReviewCloseUriMatchMenu() => _uriMatchFlyout?.Hide();
        internal bool NativeReviewListOnly => VaultListPane.Visibility == Visibility.Visible
            && VaultListPane.ActualWidth > 200 && VaultDetailPane.Visibility == Visibility.Collapsed;
        internal bool NativeReviewDetailOnly => VaultListPane.Visibility == Visibility.Collapsed
            && VaultDetailPane.Visibility == Visibility.Visible && VaultDetailPane.ActualWidth > 200
            && VaultNarrowBackButton.Visibility == Visibility.Visible && VaultNarrowBackButton.ActualWidth >= 24;
        internal double NativeReviewListWidth => VaultListPane.ActualWidth;
        internal double NativeReviewDetailWidth => VaultDetailPane.ActualWidth;
        internal void NativeReviewBackToList() => OnVaultNarrowBackClick(this, new RoutedEventArgs());
        internal bool NativeReviewDetailAnalysisCurrent => ViewModel.SelectedItem is { } item
            && _detailHealthReport is not null && _detailHealthItems.Contains(item);
        internal bool NativeReviewHealthyStatus => DetailStatusIcon.Visibility == Visibility.Visible
            && DetailStatusIcon.Glyph == "\uE930"
            && DetailStatusIcon.Foreground is Microsoft.UI.Xaml.Media.SolidColorBrush actual
            && ThemeBrushResolver.Resolve(this, "SystemFillColorSuccessBrush") is Microsoft.UI.Xaml.Media.SolidColorBrush expected
            && actual.Color.Equals(expected.Color);
        internal void NativeReviewOpenPasswordHistory() => OnPasswordHistoryClick(this, new RoutedEventArgs());
        internal void NativeReviewBringFolderIntoView() => CipherEditorFolderBox.StartBringIntoView();
        internal void NativeReviewBringCustomFieldsIntoView() => CustomFieldsEditorItems.StartBringIntoView();
        internal void NativeReviewOpenFolderFlyout()
        {
            EditorFolderFlyout.AreOpenCloseAnimationsEnabled = false;
            EditorFolderFlyout.ShowAt(CipherEditorFolderBox);
        }
        internal void NativeReviewCloseFolderFlyout() => EditorFolderFlyout.Hide();
        internal void NativeReviewScrollDetailToTop() => VaultDetailScroll.ChangeView(null, 0, null, true);
    }

    public sealed partial class SendPage
    {
        internal void NativeReviewSort(bool deletion) => OnSortClick(new RadioMenuFlyoutItem
            { Tag = deletion ? "deletion" : "recent" }, new RoutedEventArgs());
        internal bool NativeReviewRowsVisible => NativeReviewElementVisible(ListPane)
            && SendsList.Visibility == Visibility.Visible && SendsList.ActualWidth > 200 && ViewModel.FilteredItems.Count > 0;
        private double NativeReviewPixelTolerance => 1 / (XamlRoot?.RasterizationScale is > 0 and var scale ? scale : 1);
        internal bool NativeReviewSelectionMarkersValid
        {
            get
            {
                foreach (var item in ViewModel.FilteredItems)
                {
                    if (SendsList.ContainerFromItem(item) is not ListViewItem container
                        || container.ContentTemplateRoot is not Grid row
                        || row.FindName("RowSelectionIndicator") is not Border marker) return false;
                    var expected = container.IsSelected && !(_draft is not null && _editingItem is null);
                    if (!expected && marker.Visibility != Visibility.Collapsed) return false;
                    if (expected && (marker.Visibility != Visibility.Visible
                        || marker.Width != 3 || marker.Height != 16
                        || Math.Abs(marker.ActualWidth - 3) > NativeReviewPixelTolerance
                        || Math.Abs(marker.ActualHeight - 16) > NativeReviewPixelTolerance
                        || marker.Background is not Microsoft.UI.Xaml.Media.SolidColorBrush actual
                        || ThemeBrushResolver.Resolve(this, "AccentFillColorDefaultBrush") is not Microsoft.UI.Xaml.Media.SolidColorBrush accent
                        || !actual.Color.Equals(accent.Color))) return false;
                }
                return true;
            }
        }
        internal bool NativeReviewDraftHasNoListMarker => ViewModel.FilteredItems.All(item =>
            SendsList.ContainerFromItem(item) is not ListViewItem { ContentTemplateRoot: Grid row }
            || row.FindName("RowSelectionIndicator") is Border { Visibility: Visibility.Collapsed });
        internal bool NativeReviewDraftMarkerMeasured => DraftRow.Visibility == Visibility.Visible
            && DraftRow.Child is Grid grid && grid.Children.OfType<Border>().Any(marker => marker.Width == 3 && marker.Height == 16
                && marker.Visibility == Visibility.Visible && Math.Abs(marker.ActualWidth - 3) <= NativeReviewPixelTolerance
                && Math.Abs(marker.ActualHeight - 16) <= NativeReviewPixelTolerance);
        internal string NativeReviewMarkerMeasurements => string.Join("; ", ViewModel.FilteredItems.Select(item =>
            SendsList.ContainerFromItem(item) is ListViewItem { ContentTemplateRoot: Grid row }
                && row.FindName("RowSelectionIndicator") is Border marker
                ? $"id={item.Id}, visible={marker.Visibility}, declared={marker.Width}x{marker.Height}, actual={marker.ActualWidth:F3}x{marker.ActualHeight:F3}"
                : $"id={item.Id}, unrealized"));
        internal Task NativeReviewRefreshAsync() => LoadInitialAsync();
        internal bool NativeReviewEmptyContentVisible => NativeReviewElementVisible(SendEmptyPanel)
            && SendEmptyPanel.ActualWidth >= 100 && SendEmptyPanel.ActualHeight >= 40;
        internal bool NativeReviewEmptyCreateActionVisible => NativeReviewElementVisible(AddSendButton)
            && AddSendButton.ActualWidth >= 60 && AddSendButton.ActualHeight >= 24;
        internal bool NativeReviewEmptySearchActionVisible => SendEmptyPanel.Children.OfType<StackPanel>()
            .Where(NativeReviewElementVisible).SelectMany(panel => panel.Children.OfType<Button>())
            .Any(button => NativeReviewElementVisible(button) && button.ActualWidth >= 60 && button.ActualHeight >= 24);

        private static bool NativeReviewElementVisible(FrameworkElement element)
        {
            if (!element.IsLoaded) return false;
            for (DependencyObject? node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                if (node is FrameworkElement { Visibility: Visibility.Collapsed }) return false;
            return true;
        }
        internal bool NativeReviewListOnly => ListPane.Visibility == Visibility.Visible && ListPane.ActualWidth > 200
            && DetailPane.Visibility == Visibility.Collapsed;
        internal bool NativeReviewDetailOnly => ListPane.Visibility == Visibility.Collapsed
            && DetailPane.Visibility == Visibility.Visible && DetailPane.ActualWidth > 200
            && NarrowBackBar.Visibility == Visibility.Visible && BackToListButton.ActualWidth >= 24;
        internal double NativeReviewListWidth => ListPane.ActualWidth;
        internal double NativeReviewDetailWidth => DetailPane.ActualWidth;
        internal void NativeReviewBackToList() => OnBackToListClick(this, new RoutedEventArgs());
        internal string? NativeReviewSelectedId => _selectedItem?.Id;
        internal bool NativeReviewSelectedRowMeasured => SendsList.SelectedItem is SendListItem item
            && item.Id == _selectedItem?.Id && SendsList.ContainerFromItem(item) is ListViewItem { IsSelected: true, ActualWidth: > 200 };
        internal bool NativeReviewItemClickEnabled => SendsList.IsItemClickEnabled;
        internal bool NativeReviewDetailVisible => DetailPane.Visibility == Visibility.Visible
            && DetailScroll.Visibility == Visibility.Visible && DetailScroll.ActualWidth > 200;
        internal bool NativeReviewDetailMatches(SendListItem expected) => _selectedItem is { } selected
            && selected.Id == expected.Id && selected.Name == expected.Name && selected.AccessCount == expected.AccessCount
            && selected.RevisionDate == expected.RevisionDate && DetailName.Text == expected.Name
            && DetailAccessCount.Text.Contains(expected.AccessCount.ToString(), StringComparison.Ordinal)
            && SelectedMoreButton.DataContext is SendListItem menuItem && menuItem.Id == expected.Id
            && menuItem.Name == expected.Name && menuItem.AccessCount == expected.AccessCount && menuItem.RevisionDate == expected.RevisionDate;
        internal Task NativeReviewReopenSameItemAsync() => _selectedItem is { } item
            ? ReopenSelectedDetailAsync(item) : Task.CompletedTask;
        internal void NativeReviewSelect(SendType type)
        {
            var item = ViewModel.FilteredItems.FirstOrDefault(candidate => candidate.Type == type);
            if (item is not null && (SendsList.SelectedItem as SendListItem)?.Id == item.Id)
                AcceptSearchResult();
            else
                SendsList.SelectedItem = item;
        }
        internal Task NativeReviewEditSelectedAsync() => EditItemAsync(_selectedItem);
        internal bool NativeReviewEditorOpen => _draft is not null;
        internal void NativeReviewResetEditor()
        {
            _editorDirty = false;
            CloseEditor();
        }
        internal void NativeReviewSetDraft(string name, string text)
        {
            if (_draft is null) throw new InvalidOperationException("The synthetic editor is not open.");
            Draft.Name = name;
            Draft.Text = text;
            Bindings.Update();
        }
    }

    public sealed partial class SettingsPage
    {
        internal void NativeReviewSelectSection(string section)
        {
            MainWindow.NativeReviewDisableNavigationTransitions(SettingsContentFrame);
            SelectSection(section);
        }

        internal void NativeReviewShowTwoFactor()
        {
            NativeReviewSelectSection("security");
            ShowSubpage(TwoFactorSummaryCard.Header?.ToString() ?? nameof(SettingsTwoFactorPage));
            var page = new SettingsTwoFactorPage();
            page.Initialize(ViewModel, () => Task.CompletedTask, () => Task.CompletedTask,
                () => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask,
                _ => Task.CompletedTask, null, () => Task.CompletedTask);
            SettingsContentFrame.Content = page;
        }

        internal void NativeReviewShowPassword()
        {
            NativeReviewSelectSection("account");
            OnChangePasswordCardClick(this, new RoutedEventArgs());
        }

        internal void NativeReviewShowAccountManagement()
        {
            NativeReviewSelectSection("account");
            OnAccountManagementClick(this, new RoutedEventArgs());
        }

        internal AccountManagementPage? NativeReviewAccountManagement => SettingsContentFrame.Content as AccountManagementPage;
        internal ImportExportPage? NativeReviewImportExport => SettingsContentFrame.Content as ImportExportPage;
    }

    public sealed partial class AccountManagementPage
    {
        internal bool NativeReviewFieldsMeasured => VerificationTokenBox.ActualWidth >= 120
            && NewEmailBox.ActualWidth >= 120 && EmailPasswordBox.ActualWidth >= 120;
        internal bool NativeReviewInitialSecurityState => ViewModel.CanRequestEmailChange
            && !ViewModel.CanConfirmEmailChange && !EmailCodeBox.IsEnabled
            && string.IsNullOrEmpty(EmailPasswordBox.Password) && string.IsNullOrEmpty(RevokePasswordBox.Password);
    }

    public sealed partial class LoginPage
    {
        internal Task NativeReviewSubmitAsync() => SubmitAuthenticationAsync();
        internal bool NativeReviewDeviceLoginIconMeasured => LoginWithDeviceButton.Content is StackPanel panel
            && panel.Children.OfType<FontIcon>().Any(icon => icon.Glyph == "\uE772" && icon.FontSize == 16
                && icon.ActualWidth >= 14 && icon.ActualHeight >= 14);
        internal bool NativeReviewDeviceLoginActionNamed => LoginWithDeviceButton.Content is StackPanel panel
            && panel.Children.OfType<TextBlock>().FirstOrDefault() is { } label
            && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(LoginWithDeviceButton) == label.Text;
        internal bool NativeReviewDeviceLoginActionVisible => LoginWithDeviceButton.IsLoaded
            && LoginWithDeviceButton.Visibility == Visibility.Visible && LoginWithDeviceButton.IsEnabled
            && PasswordAlternativeMethods.Visibility == Visibility.Visible
            && LoginWithDeviceButton.ActualWidth >= 200 && LoginWithDeviceButton.ActualHeight >= 24;
    }

    internal sealed partial class DialogPasswordPrompt
    {
        internal bool NativeReviewHasPending => _pending is not null;
        internal bool NativeReviewPasswordCleared => string.IsNullOrEmpty(_password.Password);
        internal void NativeReviewComplete(string? password)
        {
            _password.Password = password ?? string.Empty;
            _pending?.TrySetResult(_isCurrent() ? password : null);
            _password.Password = string.Empty;
        }
    }
}

namespace App.Controls
{
    public sealed partial class CopyStrip
    {
        internal Task NativeReviewRevealPasswordAsync() => RevealAsync("password");
        internal void NativeReviewRenderAgain() => RenderValues();
        internal string NativeReviewPasswordNativeText => _cells.First(cell => cell.Key == "password").ValueText.Text;
        internal string NativeReviewPasswordInlineText => string.Concat(_cells.First(cell => cell.Key == "password")
            .ValueText.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Select(run => run.Text));
        internal Func<bool> NativeReviewCaptureClearCheck()
        {
            var blocks = _cells.Select(cell => cell.ValueText).ToArray();
            return () => blocks.All(block => string.IsNullOrEmpty(block.Text)
                && block.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().All(run => string.IsNullOrEmpty(run.Text)));
        }
    }

    public sealed partial class FieldRow
    {
        internal double NativeReviewCopyButtonWidth => CopyButton.ActualWidth;
        internal bool NativeReviewCopyLabelVisible => CopyLabel.Visibility == Visibility.Visible;
        internal string NativeReviewCopyActionName => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(CopyButton);
        internal void NativeReviewRenderAgain() => Render();
        internal string NativeReviewVisibleText => string.Concat(ValueText.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Select(run => run.Text));
        internal string NativeReviewNativeText => ValueText.Text;
        internal void NativeReviewToggleReveal() => OnReveal(this, new RoutedEventArgs());
        internal void NativeReviewCopy() => OnCopy(this, new RoutedEventArgs());
        internal bool NativeReviewRevealed => _revealed;
        internal bool NativeReviewBusy => _revealBusy;
        internal bool NativeReviewCopyBusy => _copyBusy;
        internal bool NativeReviewHasValue => !string.IsNullOrEmpty(Value);
        internal bool NativeReviewIsMasked => NativeReviewVisibleText == "••••••••";
    }
}
