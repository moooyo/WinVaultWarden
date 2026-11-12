using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Serialization;
using App.Controls;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace App.NativeUiReview;

internal sealed class NativeReviewRunner(ReviewOptions options, ReviewFixture fixture, MainWindow window)
{
    private readonly ReviewManifest _manifest = CreateManifest(options);

    private static ReviewManifest CreateManifest(ReviewOptions options)
    {
        NativeReviewStartupTrace.Record("runner-binary-identity-started");
        var mvid = ReviewOptions.ReviewMvid;
        NativeReviewStartupTrace.Record("runner-mvid-read");
        var sha256 = ReviewOptions.HashFile(ReviewOptions.ReviewBinaryPath);
        NativeReviewStartupTrace.Record("runner-binary-hashed");
        var sdkVersion = typeof(NativeReviewRunner).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "WindowsAppSDKVersion")?.Value ?? "unknown";
        NativeReviewStartupTrace.Record("runner-sdk-metadata-read");
        return new ReviewManifest
        {
            Round = options.Round,
            StartedAt = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            AssemblyMvid = mvid,
            AssemblySha256 = sha256,
            BinaryPathKind = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "managed-assembly" : "native-executable",
            DynamicCodeSupported = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
            DynamicCodeCompiled = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled,
            WindowsAppSdkVersion = sdkVersion,
            RunProfile = options.LocalizationProbeOnly ? "localization-probe" : options.PopupProbeOnly ? "popup-probe" : options.EssentialOnly ? "essential" : "full",
            ExpectedMatrixCells = options.LocalizationProbeOnly ? 3 : options.PopupProbeOnly ? 1 : options.EssentialOnly ? 6 : 8,
            FixtureLanguage = options.LanguagePreference,
            FixtureDataMode = options.FixtureDataMode,
        };
    }
    private string _viewport = string.Empty;
    private string _theme = string.Empty;
    private string _case = string.Empty;

    internal async Task RunAsync()
    {
        NativeReviewStartupTrace.Record("runner-entered");
        await WaitUntilAsync(() => window.NativeReviewRoot.IsLoaded && window.NativeReviewRoot.XamlRoot is not null,
            "native-shell-loaded");
        NativeReviewStartupTrace.Record("runner-native-root-loaded");
        Assert("fixture-localized-resource-runtime", global::App.Localization.AppLocalization.ResourcesAvailable
            && global::App.Localization.AppLocalization.CurrentPreference == options.LanguagePreference,
            $"Synthetic in-memory {options.LanguagePreference} preferences are applied through process-local MRT Core before production XAML is created.");
        NativeReviewStartupTrace.Record("runner-localized-resource-assertion-completed");
        Assert("fixture-declared-data-shape", options.FixtureDataMode switch
        {
            "empty" => fixture.Session.Ciphers.Count == 0 && fixture.Session.Folders.Count == 0,
            "single-login" => fixture.Session.Ciphers.Count == 1 && fixture.Session.Ciphers[0].Type == Core.Enums.CipherType.Login
                && fixture.Session.Folders.Count == 0,
            _ => fixture.Session.Ciphers.Count == 13 && fixture.Session.Folders.Count == 3,
        }, $"declared={options.FixtureDataMode}; ciphers={fixture.Session.Ciphers.Count}; folders={fixture.Session.Folders.Count}");
        NativeReviewStartupTrace.Record("runner-source-inventory-started");
        var sourceFiles = await Task.Run(() => Directory.EnumerateFiles(Path.Combine(options.SourceRoot, "src", "App"), "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml" or ".csproj" or ".resw" or ".json")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new ReviewSourceFile(Path.GetRelativePath(options.SourceRoot, path).Replace('\\', '/'), ReviewOptions.HashFile(path)))
            .ToArray());
        _manifest.SourceFiles.AddRange(sourceFiles);
        NativeReviewStartupTrace.Record("runner-source-inventory-finished");
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            foreach (var viewport in new[] { (Width: 1280, Height: 800), (Width: 1440, Height: 900), (Width: 920, Height: 720), (Width: 720, Height: 720) })
            {
                if (options.EssentialOnly && viewport.Width == 1440) continue;
                if (options.LocalizationProbeOnly && (theme != ElementTheme.Light || viewport.Width == 1440)) continue;
                if (options.PopupProbeOnly && (theme != ElementTheme.Light || viewport.Width != 1280)) continue;
                _viewport = $"{viewport.Width}x{viewport.Height}";
                _theme = theme.ToString().ToLowerInvariant();
                NativeReviewStartupTrace.Record("runner-before-reopen-session");
                fixture.ReopenSession();
                NativeReviewStartupTrace.Record("runner-session-reopened");
                fixture.Vault.RefreshFromSnapshot();
                NativeReviewStartupTrace.Record("runner-vault-snapshot-refreshed");
                window.NativeReviewShowVault();
                NativeReviewStartupTrace.Record("runner-vault-view-shown");
                await SettleAsync();
                NativeReviewStartupTrace.Record("runner-first-view-settled");
                AppPreferences.Current.ThemeIndex = theme == ElementTheme.Dark ? 2 : 1;
                ThemeManager.Apply(AppPreferences.Current.ThemeIndex);
                await SettleAsync();
                window.NativeReviewSetViewport(viewport.Width, viewport.Height, theme);
                await SettleAsync();
                window.NativeReviewCorrectViewport(viewport.Width, viewport.Height);
                await SettleAsync();
                Assert("viewport-width", Math.Abs(window.NativeReviewRoot.ActualWidth - viewport.Width) <= 1,
                    $"expected={viewport.Width}; actual={window.NativeReviewRoot.ActualWidth:F2}");
                Assert("viewport-height", Math.Abs(window.NativeReviewRoot.ActualHeight - viewport.Height) <= 1,
                    $"expected={viewport.Height}; actual={window.NativeReviewRoot.ActualHeight:F2}");
                if (options.LocalizationProbeOnly)
                {
                    var expectedVault = options.LanguagePreference switch
                    {
                        "zh-CN" => "密码库",
                        "zh-TW" => "保險庫",
                        "ja-JP" => "ボールト",
                        _ => "Vault",
                    };
                    var keyName = "Text" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("密码库")))[..16];
                    var typedKey = Enum.Parse<global::App.Localization.UiStringKey>(keyName);
                    var typedLabel = global::App.Localization.UiText.Get(typedKey);
                    Assert("localized-xuid-and-typed-label-agree", window.NativeReviewVaultTabLabel == expectedVault
                        && typedLabel == expectedVault,
                        $"language={options.LanguagePreference}; xuid={window.NativeReviewVaultTabLabel}; typed={typedLabel}; expected={expectedVault}");
                    Assert("localized-root-language", window.NativeReviewRoot.Language == options.LanguagePreference
                        && global::App.Localization.UiText.DisplayCulture.Name == options.LanguagePreference,
                        $"root={window.NativeReviewRoot.Language}; culture={(global::App.Localization.UiText.DisplayCulture.Name)}");
                    Assert("localized-tabs-retain-keyboard-labels", window.NativeReviewTabKeyboardLabelsValid,
                        "Every expanded or compact title tab remains enabled, keyboard focusable, explicitly named and described by a matching localized tooltip.");
                    await CaptureAsync($"localization-{options.LanguagePreference.ToLowerInvariant()}");
                    if (viewport.Width == 1280)
                    {
                        NativeReviewStartupTrace.Record("localization-first-frame-captured");
                        window.NativeReviewRefreshShell();
                        await SettleAsync();
                        Assert("post-frame-native-navigation-refresh", window.NativeReviewTabKeyboardLabelsValid
                            && window.NativeReviewVaultTabLabel == expectedVault,
                            "A normal production folder/navigation refresh completes after the first native frame without losing keyboard labels.");
                        await CaptureAsync($"localization-{options.LanguagePreference.ToLowerInvariant()}-after-navigation-refresh");
                    }
                    _manifest.CompletedMatrixCells.Add($"{_theme}/{_viewport}");
                    continue;
                }
                if (options.PopupProbeOnly)
                {
                    await ReviewPopupProbeAsync();
                    _manifest.CompletedMatrixCells.Add($"{_theme}/{_viewport}");
                    continue;
                }
                await ReviewVaultAsync();
                await ReviewGeneratorAsync();
                await ReviewSendAsync();
                await ReviewReportsAsync();
                await ReviewSettingsAsync();
                await ReviewAuthenticationAsync();
                _manifest.CompletedMatrixCells.Add($"{_theme}/{_viewport}");
            }
        }
        Assert("matrix-cell-count", _manifest.CompletedMatrixCells.Distinct(StringComparer.Ordinal).Count() == _manifest.ExpectedMatrixCells,
            $"expected={_manifest.ExpectedMatrixCells}; completed={_manifest.CompletedMatrixCells.Count}");
        Assert("fixture-no-platform-or-account-side-effect-actions", fixture.NoExternalCapabilityActions,
            "SSH, native provider, startup registration and account-management actions remain unused; their fixture dependencies are memory-only fakes.");
        _manifest.CompletedAt = DateTimeOffset.UtcNow;
        _manifest.Completed = true;
        _manifest.Passed = _manifest.Assertions.All(assertion => assertion.Passed);
        SaveManifest("manifest.json");
        if (!_manifest.Passed)
            throw new InvalidOperationException("One or more native semantic assertions failed.");
    }

    private async Task<VaultPage> PrepareVaultAsync()
    {
        fixture.Vault.CancelEdit();
        fixture.Vault.SearchText = string.Empty;
        fixture.Vault.SelectedTypeIndex = 0;
        window.NativeReviewNavigate("vault");
        await WaitUntilAsync(() => window.NativeReviewFrame.Content is VaultPage { IsLoaded: true }, "vault-page-loaded");
        var page = (VaultPage)window.NativeReviewFrame.Content;
        fixture.Vault.SelectFilterByTag("vault:allitems");
        return page;
    }

    private async Task ReviewPopupProbeAsync()
    {
        var page = await PrepareVaultAsync();
        await SelectVaultDetailAsync(page, VaultItemKind.Login);
        await fixture.Vault.BeginEditAsync(fixture.DetailIds[VaultItemKind.Login], _ => Task.FromResult<string?>(ReviewVerifier.SyntheticPhrase));
        page.NativeReviewBringFolderIntoView();
        await SettleAsync();
        page.NativeReviewOpenFolderFlyout();
        await Task.Delay(320);
        await CaptureAsync("vault-editor-folder-flyout");
        var index = 0;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(window.NativeReviewRoot.XamlRoot).Where(popup => popup.IsOpen))
        {
            foreach (var candidate in Descendants<FrameworkElement>(popup.Child).Where(element => element is Border or ScrollViewer or TextBlock).Take(4))
                await RenderAsync(candidate, Path.Combine(options.OutputDirectory, $"native-popup-fragment-{index++}.png"), requireVariation: false);
        }
        page.NativeReviewCloseFolderFlyout();
        await Task.Delay(320);
        fixture.Vault.CancelEdit();
    }

    private async Task SelectVaultDetailAsync(VaultPage page, VaultItemKind kind)
    {
        fixture.Vault.CancelEdit();
        fixture.Vault.SelectFilterByTag("vault:allitems");
        fixture.Vault.SelectedItem = fixture.Vault.Items.First(item => item.Id == fixture.DetailIds[kind]);
        await WaitUntilAsync(() => fixture.Vault.Detail?.Kind == kind, $"detail-{kind}-loaded");
        page.NativeReviewScrollDetailToTop();
        await SettleAsync();
    }

    private async Task ReviewVaultAsync()
    {
        var page = await PrepareVaultAsync();
        fixture.Vault.SelectedItem = null;
        fixture.Vault.Detail = null;
        await CaptureAsync("vault-shell");
        if (window.NativeReviewRoot.ActualWidth < 800)
        {
            Assert("vault-narrow-initial-list", page.NativeReviewListOnly && Math.Abs(page.NativeReviewListWidth - page.ActualWidth) < 2,
                $"listWidth={page.NativeReviewListWidth:F2}; pageWidth={page.ActualWidth:F2}");
            await SelectVaultDetailAsync(page, VaultItemKind.Login);
            await WaitUntilAsync(() => page.NativeReviewDetailOnly, "vault-narrow-detail-visible");
            Assert("vault-narrow-detail-fills-workspace", Math.Abs(page.NativeReviewDetailWidth - page.ActualWidth) < 2,
                $"detailWidth={page.NativeReviewDetailWidth:F2}; pageWidth={page.ActualWidth:F2}");
            await CaptureAsync("vault-narrow-selected-detail");
            page.NativeReviewBackToList();
            await WaitUntilAsync(() => page.NativeReviewListOnly && fixture.Vault.Detail is null, "vault-narrow-back-restores-list");
            await CaptureAsync("vault-narrow-list-after-back");
        }
        foreach (var kind in Enum.GetValues<VaultItemKind>())
        {
            await SelectVaultDetailAsync(page, kind);
            Assert($"detail-kind-{kind}", fixture.Vault.Detail?.Kind == kind, "The production detail template is selected.");
            if (kind == VaultItemKind.Login)
            {
                await VerifyWebsiteRenderingAsync(page);
                await VerifyCopyStripRenderingAsync(page);
            }
            if (kind == VaultItemKind.Note)
            {
                var note = Descendants<FieldRow>(page).Single(field => field.IsMultiline && !field.ShowCopyButtonText && !string.IsNullOrEmpty(field.CopyButtonText));
                Assert("note-copy-action-icon-only", !note.NativeReviewCopyLabelVisible && note.NativeReviewCopyButtonWidth is >= 24 and <= 40,
                    $"nativeButtonWidth={note.NativeReviewCopyButtonWidth:F2}");
                Assert("note-copy-action-semantic-label", note.NativeReviewCopyActionName == note.CopyButtonText,
                    "The icon-only native note action retains the full production copy-all accessible label.");
            }
            await CaptureAsync($"vault-detail-{kind.ToString().ToLowerInvariant()}");
        }
        await SelectVaultDetailAsync(page, VaultItemKind.Login);
        await CaptureAsync("vault-detail-login-returned");
        fixture.Vault.SelectedItem = fixture.Vault.Items.First(item => item.Id == fixture.HealthyLoginId);
        await WaitUntilAsync(() => fixture.Vault.Detail?.Id == fixture.HealthyLoginId && page.NativeReviewDetailAnalysisCurrent,
            "healthy-login-current-analysis");
        Assert("healthy-login-green-status-glyph", page.NativeReviewHealthyStatus,
            "The analyzed synthetic login with a unique strong password displays the production success-colored shield glyph.");
        page.NativeReviewScrollDetailToTop();
        await CaptureAsync("vault-detail-login-healthy");
        page.BeginNewItem(VaultItemKind.Login);
        await WaitUntilAsync(() => fixture.Vault.IsEditing, "new-login-editor-open");
        fixture.Vault.EditorDraft!.Name = "Example service";
        fixture.Vault.EditorDraft.Login.Username = "review@example.invalid";
        fixture.Vault.EditorDraft.Login.Password = "Synthetic-only-42!";
        fixture.Vault.EditorDraft.Login.PrimaryUri = "https://service.example.invalid/account";
        await SettleAsync();
        Assert("new-login-editor-first-screen-structure", page.NativeReviewLoginEditorStructureValid,
            "The native editor has measured compact credential rows, a secondary TOTP hint and no repeated login group heading or URI Expander.");
        await CaptureAsync("vault-new-login");

        fixture.Vault.CancelEdit();
        await SelectVaultDetailAsync(page, VaultItemKind.Login);
        var edit = await fixture.Vault.BeginEditAsync(fixture.DetailIds[VaultItemKind.Login],
            _ => Task.FromResult<string?>(ReviewVerifier.SyntheticPhrase));
        Assert("edit-login-authorized", edit.IsSuccess, $"status={edit.Status}");
        await SettleAsync();
        Assert("edit-login-editor-first-screen-structure", page.NativeReviewLoginEditorStructureValid,
            "The production edit template uses the same measured compact row structure.");
        await CaptureAsync("vault-edit-login");
        await ReviewUriMatchingAsync(page);
        page.NativeReviewBringCustomFieldsIntoView();
        await CaptureAsync("vault-edit-custom-fields");
        page.NativeReviewBringFolderIntoView();
        await SettleAsync();
        page.NativeReviewOpenFolderFlyout();
        await Task.Delay(320);
        await CaptureAsync("vault-editor-folder-flyout");
        page.NativeReviewCloseFolderFlyout();
        await SettleAsync();
        fixture.Vault.CancelEdit();
        await SelectVaultDetailAsync(page, VaultItemKind.Login);
        await CaptureAsync("vault-detail-login-restored");
        await ReviewPasswordHistoryAsync(page);
    }

    private async Task ReviewUriMatchingAsync(VaultPage page)
    {
        var draft = fixture.Vault.EditorDraft!;
        var uri = draft.Login.Uris[0];
        var originalValues = draft.Login.Uris.Select(item => item.Uri).ToArray();
        var originalMatch = uri.Match;
        uri.Match = 99;
        page.NativeReviewBringUriMatchIntoView(uri);
        await SettleAsync();
        page.NativeReviewOpenUriMatchMenu(uri);
        await WaitUntilAsync(() => page.NativeReviewUriMatchMenuReady && HasOpenPopups(), "uri-match-native-menu-open");
        Assert("uri-match-unknown-value-presented", page.NativeReviewUnknownUriRuleShown(uri) && uri.Match == 99,
            "The real native menu presents all seven known rules while retaining an unknown synthetic server rule.");
        await CaptureAsync("vault-editor-uri-match-unknown");
        page.NativeReviewCloseUriMatchMenu();
        await WaitUntilAsync(() => !HasOpenPopups() && page.NativeReviewUriMatchMenuClosed, "uri-match-cancel-closed");
        Assert("uri-match-cancel-preserves-values", uri.Match == 99 && draft.Login.Uris.Select(item => item.Uri).SequenceEqual(originalValues),
            "Closing the native rule menu does not change the unknown rule or any URI value.");
        await CaptureAsync("vault-editor-uri-match-cancelled");
        page.NativeReviewOpenUriMatchMenu(uri);
        await WaitUntilAsync(() => page.NativeReviewUriMatchMenuReady && HasOpenPopups(), "uri-match-native-menu-reopened");
        await SettleAsync();
        Assert("uri-match-reopen-preserves-unknown", uri.Match == 99 && page.NativeReviewUnknownUriRuleShown(uri),
            "Reopening the real menu preserves the unknown server rule.");
        var choice = page.NativeReviewUriRuleItem(4);
        page.NativeReviewCloseUriMatchMenu();
        await WaitUntilAsync(() => !HasOpenPopups() && page.NativeReviewUriMatchMenuClosed, "uri-match-before-click-close-finished");
        page.NativeReviewApplyUriRule(choice);
        Assert("uri-match-production-click-updates-rule", uri.Match == 3 && draft.Login.Uris.Select(item => item.Uri).SequenceEqual(originalValues),
            "The production typed-tag click handler accepts normal close-before-click ordering and updates only the selected rule.");
        page.NativeReviewOpenUriMatchMenu(uri);
        await WaitUntilAsync(() => page.NativeReviewUriMatchMenuReady && HasOpenPopups(), "uri-match-selected-menu-open");
        await SettleAsync();
        Assert("uri-match-selected-rule-checked", page.NativeReviewUriRuleItem(4).IsChecked,
            "The native menu checks the selected exact-match rule.");
        await CaptureAsync("vault-editor-uri-match-selected");
        page.NativeReviewCloseUriMatchMenu();
        await WaitUntilAsync(() => !HasOpenPopups() && page.NativeReviewUriMatchMenuClosed, "uri-match-selected-menu-closed");
        uri.Match = originalMatch;
    }

    private async Task ReviewPasswordHistoryAsync(VaultPage page)
    {
        AppPreferences.Current.SensitiveActionVerificationIndex = 0;
        fixture.SensitiveActions.EndGracePeriod();
        fixture.Verifier.Mode = ReviewVerifierMode.Allow;
        page.NativeReviewOpenPasswordHistory();
        await WaitUntilAsync(() => OpenDialogs().Count == 1, "password-history-dialog-open");
        var dialog = OpenDialogs().Single();
        var rows = Descendants<FieldRow>(dialog).Where(row => row.IsTrailingLabel).ToArray();
        Assert("password-history-count", rows.Length is > 0 and <= 5, $"rows={rows.Length}");
        Assert("password-history-initially-masked", rows.All(row => row.NativeReviewIsMasked && !row.NativeReviewRevealed), "All synthetic history rows start masked.");
        await CaptureAsync("vault-password-history-masked");

        var row = rows[0];
        var before = fixture.Verifier.Calls;
        row.NativeReviewToggleReveal();
        await WaitUntilAsync(() => !row.NativeReviewBusy, "history-first-reveal-finished");
        Assert("history-first-reveal-verifies", row.NativeReviewRevealed && fixture.Verifier.Calls == before + 1, "Exactly one synthetic verifier call is required.");
        await CaptureAsync("vault-password-history-revealed");
        row.NativeReviewToggleReveal();
        before = fixture.Verifier.Calls;
        row.NativeReviewToggleReveal();
        await WaitUntilAsync(() => !row.NativeReviewBusy, "history-repeat-reveal-finished");
        Assert("history-each-reveal-verifies", row.NativeReviewRevealed && fixture.Verifier.Calls == before + 1, "The next reveal requires verification in per-action mode.");

        row.NativeReviewToggleReveal();
        fixture.Verifier.Mode = ReviewVerifierMode.Deny;
        before = fixture.Verifier.Calls;
        row.NativeReviewToggleReveal();
        await WaitUntilAsync(() => !row.NativeReviewBusy, "history-denied-reveal-finished");
        Assert("history-denial-remains-masked", !row.NativeReviewRevealed && row.NativeReviewIsMasked && fixture.Verifier.Calls == before + 1,
            "A denied synthetic verification must not display the history value.");
        await CaptureAsync("vault-password-history-denied");

        var clipboardVersion = fixture.Clipboard.WriteVersion;
        row.NativeReviewCopy();
        await WaitUntilAsync(() => !row.NativeReviewCopyBusy, "history-memory-copy-finished");
        Assert("history-copy-remains-masked", !row.NativeReviewRevealed && row.NativeReviewIsMasked && fixture.Clipboard.WriteVersion == clipboardVersion + 1,
            "The production copy handler writes only to the fixture clipboard and keeps the value masked.");
        fixture.Clipboard.ClearSecretsNow();

        fixture.Verifier.Mode = ReviewVerifierMode.InlinePrompt;
        row.NativeReviewToggleReveal();
        await WaitUntilAsync(() => fixture.Verifier.LastPromptTarget is DialogPasswordPrompt { NativeReviewHasPending: true }, "history-inline-prompt-visible");
        var prompt = (DialogPasswordPrompt)fixture.Verifier.LastPromptTarget!;
        Assert("history-fallback-single-dialog", OpenDialogs().Count == 1 && ReferenceEquals(OpenDialogs()[0], dialog),
            "Fallback verification reuses the already open native ContentDialog.");
        Assert("history-fallback-still-masked", !row.NativeReviewRevealed, "The pending prompt does not reveal the value.");
        await CaptureAsync("vault-password-history-inline-fallback");
        prompt.NativeReviewComplete(null);
        await WaitUntilAsync(() => !row.NativeReviewBusy, "history-inline-cancel-finished");
        Assert("history-inline-cancel-remains-masked", row.NativeReviewIsMasked && !row.NativeReviewRevealed && prompt.NativeReviewPasswordCleared,
            "Cancellation keeps the row masked and clears the synthetic password box.");
        await CaptureAsync("vault-password-history-cancelled");

        row.NativeReviewToggleReveal();
        await WaitUntilAsync(() => fixture.Verifier.LastPromptTarget is DialogPasswordPrompt { NativeReviewHasPending: true }, "history-inline-accept-visible");
        prompt = (DialogPasswordPrompt)fixture.Verifier.LastPromptTarget!;
        prompt.NativeReviewComplete(ReviewVerifier.SyntheticPhrase);
        await WaitUntilAsync(() => !row.NativeReviewBusy, "history-inline-accept-finished");
        Assert("history-inline-accept-reveals", row.NativeReviewRevealed && prompt.NativeReviewPasswordCleared,
            "A synthetic fallback approval reveals only after completion and clears its input.");
        await CaptureAsync("vault-password-history-inline-approved");

        row.NativeReviewToggleReveal();
        AppPreferences.Current.SensitiveActionVerificationIndex = 1;
        fixture.SensitiveActions.EndGracePeriod();
        fixture.Verifier.Mode = ReviewVerifierMode.Allow;
        row.NativeReviewToggleReveal();
        await WaitUntilAsync(() => !row.NativeReviewBusy, "history-grace-first-reveal-finished");
        before = fixture.Verifier.Calls;
        row.NativeReviewToggleReveal();
        row.NativeReviewToggleReveal();
        await WaitUntilAsync(() => !row.NativeReviewBusy, "history-grace-repeated-reveal-finished");
        Assert("history-explicit-grace-reused", row.NativeReviewRevealed && fixture.Verifier.Calls == before && fixture.SensitiveActions.GraceRemaining > TimeSpan.Zero,
            "Only the explicit five-minute preference may reuse verification for this synthetic session.");
        AppPreferences.Current.SensitiveActionVerificationIndex = 0;
        fixture.SensitiveActions.EndGracePeriod();

        dialog.Hide();
        await WaitUntilAsync(() => !HasOpenPopups() && rows.All(history => !history.NativeReviewHasValue), "history-close-finishes");
        Assert("history-close-clears-values", rows.All(history => !history.NativeReviewHasValue), "Closing the native dialog clears its synthetic rows.");
        await CaptureAsync("vault-detail-login-after-history");

        fixture.Verifier.Mode = ReviewVerifierMode.Allow;
        page.NativeReviewOpenPasswordHistory();
        await WaitUntilAsync(() => OpenDialogs().Count == 1, "history-context-dialog-open");
        var contextRows = Descendants<FieldRow>(OpenDialogs().Single()).Where(history => history.IsTrailingLabel).ToArray();
        fixture.Vault.SelectedItem = fixture.Vault.Items.First(item => item.Id == fixture.DetailIds[VaultItemKind.Card]);
        await WaitUntilAsync(() => !HasOpenPopups(), "history-context-change-closes-dialog");
        Assert("history-context-change-clears-values", contextRows.Length > 0 && contextRows.All(history => !history.NativeReviewHasValue && !history.NativeReviewRevealed),
            "Changing the selected synthetic item invalidates and clears the history dialog.");
        await CaptureAsync("vault-password-history-cleared-on-selection");

        await SelectVaultDetailAsync(page, VaultItemKind.Login);
        page.NativeReviewOpenPasswordHistory();
        await WaitUntilAsync(() => OpenDialogs().Count == 1, "history-unload-dialog-open");
        var unloadRows = Descendants<FieldRow>(OpenDialogs().Single()).Where(history => history.IsTrailingLabel).ToArray();
        window.NativeReviewNavigate("generator");
        await WaitUntilAsync(() => !HasOpenPopups(), "history-host-unload-closes-dialog");
        Assert("history-host-unload-clears-values", unloadRows.Length > 0 && unloadRows.All(history => !history.NativeReviewHasValue && !history.NativeReviewRevealed),
            "Unloading the production vault page closes the dialog and clears its synthetic rows.");
        await CaptureAsync("vault-password-history-cleared-on-unload");

        page = await PrepareVaultAsync();
        await SelectVaultDetailAsync(page, VaultItemKind.Login);
        page.NativeReviewOpenPasswordHistory();
        await WaitUntilAsync(() => OpenDialogs().Count == 1, "history-lock-dialog-open");
        dialog = OpenDialogs().Single();
        rows = Descendants<FieldRow>(dialog).Where(history => history.IsTrailingLabel).ToArray();
        row = rows[0];
        fixture.Verifier.Mode = ReviewVerifierMode.InlinePrompt;
        row.NativeReviewToggleReveal();
        await WaitUntilAsync(() => fixture.Verifier.LastPromptTarget is DialogPasswordPrompt { NativeReviewHasPending: true }, "history-lock-pending-prompt");
        prompt = (DialogPasswordPrompt)fixture.Verifier.LastPromptTarget!;
        fixture.Session.Lock();
        fixture.Services.GetRequiredService<ITransientOperationRegistry>().CancelAndCloseAllSynchronously();
        fixture.Vault.ClearSensitive();
        fixture.SensitiveActions.ClearSensitive();
        await WaitUntilAsync(() => !HasOpenPopups() && !row.NativeReviewBusy, "history-lock-closes-dialog");
        Assert("history-lock-clears-values", rows.All(history => !history.NativeReviewHasValue && !history.NativeReviewRevealed) && prompt.NativeReviewPasswordCleared,
            "Locking the synthetic session cancels pending verification and clears every history row.");
        window.NativeReviewShowAuth(registration: false);
        await CaptureAsync("vault-password-history-cleared-after-lock");
        fixture.Verifier.Mode = ReviewVerifierMode.Allow;
        fixture.ReopenSession();
        fixture.Vault.RefreshFromSnapshot();
        window.NativeReviewShowVault();
        await SettleAsync();
    }

    private async Task VerifyWebsiteRenderingAsync(VaultPage page)
    {
        var field = Descendants<FieldRow>(page).First(row => row.ShowOpen && row.Value.Contains("?mode=review#details", StringComparison.Ordinal));
        var original = field.Value;
        var uri = new Uri(original);
        var expected = (uri.IsDefaultPort ? uri.Host : uri.Authority)
            + (uri.PathAndQuery == "/" ? string.Empty : uri.PathAndQuery) + uri.Fragment;
        for (var index = 0; index < 8; index++) field.NativeReviewRenderAgain();
        Assert("website-repeat-render-single-path", field.NativeReviewVisibleText == expected,
            "Repeated production FieldRow rendering preserves a single path, query and fragment.");
        var theme = window.NativeReviewRoot.RequestedTheme;
        window.NativeReviewRoot.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        await SettleAsync();
        window.NativeReviewRoot.RequestedTheme = theme;
        await SettleAsync();
        Assert("website-theme-render-single-path", field.NativeReviewVisibleText == expected,
            "Actual-theme changes preserve a single website path, query and fragment.");
        var before = fixture.Clipboard.WriteVersion;
        field.NativeReviewCopy();
        await WaitUntilAsync(() => !field.NativeReviewCopyBusy, "website-memory-copy-finished");
        Assert("website-copy-original-uri", fixture.Clipboard.WriteVersion == before + 1 && fixture.Clipboard.LastWriteMatches(original),
            "The production copy handler sends the original synthetic URI, including its scheme, query and fragment, to the fixture clipboard.");
        fixture.Clipboard.ClearSecretsNow();
    }

    private void ObserveWebsiteFields(string stage, VaultPage page)
    {
        var fields = Descendants<FieldRow>(page).Where(field => field.ShowOpen && field.IsLoaded
            && field.Visibility == Visibility.Visible && field.ActualWidth > 0).ToArray();
        if (options.FixtureDataMode == "single-login")
        {
            Assert("website-absent-for-uri-free-control", fields.Length == 0,
                "The explicit minimal-login control has no URIs and must not invent a website field.");
            return;
        }
        Assert("website-rendered-fields-present", fields.Length > 0,
            "The selected production login template has rendered native website fields.");
        foreach (var field in fields)
        {
            if (!Uri.TryCreate(field.Value, UriKind.Absolute, out var uri)) continue;
            var expected = (uri.IsDefaultPort ? uri.Host : uri.Authority)
                + (uri.PathAndQuery == "/" ? string.Empty : uri.PathAndQuery) + uri.Fragment;
            var nativeText = field.NativeReviewNativeText;
            var inlineText = field.NativeReviewVisibleText;
            _manifest.WebsiteObservations.Add(new ReviewWebsiteObservation(stage, _theme, _viewport,
                field.Value, expected, nativeText, inlineText));
            Assert("website-native-text-single-path", nativeText == expected,
                "The native TextBlock.Text getter preserves exactly one synthetic URI path, query and fragment at this lifecycle stage.");
            Assert("website-inline-text-single-path", inlineText == expected,
                "The native inline runs preserve exactly one synthetic URI path, query and fragment at this lifecycle stage.");
        }
    }

    private async Task VerifyCopyStripRenderingAsync(VaultPage page)
    {
        var strip = Descendants<CopyStrip>(page).Single(control => control.Detail?.Id == fixture.DetailIds[VaultItemKind.Login]);
        var expected = ((LoginDetail)fixture.Vault.Detail!).Password!;
        fixture.Verifier.Mode = ReviewVerifierMode.Allow;
        var before = fixture.Verifier.Calls;
        await strip.NativeReviewRevealPasswordAsync();
        for (var index = 0; index < 8; index++) strip.NativeReviewRenderAgain();
        Assert("copystrip-reveal-verifies", fixture.Verifier.Calls == before + 1,
            "The production CopyStrip reveal uses the synthetic verifier in per-action mode.");
        Assert("copystrip-repeat-render-single-value", strip.NativeReviewPasswordNativeText == expected && strip.NativeReviewPasswordInlineText == expected,
            $"expectedLength={expected.Length}; nativeLength={strip.NativeReviewPasswordNativeText.Length}; inlineLength={strip.NativeReviewPasswordInlineText.Length}");
        var theme = window.NativeReviewRoot.RequestedTheme;
        window.NativeReviewRoot.RequestedTheme = theme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        await SettleAsync();
        window.NativeReviewRoot.RequestedTheme = theme;
        await SettleAsync();
        Assert("copystrip-theme-render-single-value", strip.NativeReviewPasswordNativeText == expected && strip.NativeReviewPasswordInlineText == expected,
            "Actual-theme changes preserve exactly one synthetic revealed password.");
        await CaptureAsync("vault-login-password-revealed");
        await strip.NativeReviewRevealPasswordAsync();
        Assert("copystrip-hide-clears-visible-password", strip.NativeReviewPasswordNativeText == "••••••••" && strip.NativeReviewPasswordInlineText == "••••••••",
            "Hiding the synthetic password removes every plaintext character from the native TextBlock and inline runs.");
        await CaptureAsync("vault-login-password-hidden");
        var oldCellsCleared = strip.NativeReviewCaptureClearCheck();
        await SelectVaultDetailAsync(page, VaultItemKind.Card);
        Assert("copystrip-context-change-clears-runs", oldCellsCleared(),
            "Replacing the production CopyStrip detail clears the old native text and every persistent inline run.");
        await SelectVaultDetailAsync(page, VaultItemKind.Login);
    }

    private async Task ReviewGeneratorAsync()
    {
        window.NativeReviewNavigate("generator");
        await WaitUntilAsync(() => window.NativeReviewFrame.Content is GeneratorPage { IsLoaded: true }, "generator-page-loaded");
        var generator = fixture.Services.GetRequiredService<GeneratorViewModel>();
        generator.Mode = GeneratorMode.Password;
        window.NativeReviewRefreshShell();
        await CaptureAsync("generator-password");
        generator.Mode = GeneratorMode.Passphrase;
        window.NativeReviewRefreshShell();
        await Task.Delay(420);
        await CaptureAsync("generator-passphrase");
    }

    private async Task ReviewSendAsync()
    {
        window.NativeReviewNavigate("send");
        await WaitUntilAsync(() => window.NativeReviewFrame.Content is SendPage { IsLoaded: true } send && send.ViewModel.LoadState != DataPageState.Loading, "send-page-loaded");
        var page = (SendPage)window.NativeReviewFrame.Content;
        await CaptureAsync("send-list");
        if (window.NativeReviewRoot.ActualWidth < 800)
        {
            Assert("send-narrow-initial-list", page.NativeReviewListOnly && Math.Abs(page.NativeReviewListWidth - page.ActualWidth) < 2,
                $"listWidth={page.NativeReviewListWidth:F2}; pageWidth={page.ActualWidth:F2}");
            page.NativeReviewSelect(SendType.Text);
            await WaitUntilAsync(() => page.NativeReviewDetailOnly, "send-narrow-detail-visible");
            var retainedId = page.NativeReviewSelectedId;
            Assert("send-narrow-detail-fills-workspace", Math.Abs(page.NativeReviewDetailWidth - page.ActualWidth) < 2,
                $"detailWidth={page.NativeReviewDetailWidth:F2}; pageWidth={page.ActualWidth:F2}");
            await CaptureAsync("send-narrow-selected-detail");
            page.NativeReviewBackToList();
            await WaitUntilAsync(() => page.NativeReviewListOnly, "send-narrow-back-restores-list");
            await SettleAsync();
            Assert("send-back-retains-native-selection", page.NativeReviewSelectedId == retainedId && page.NativeReviewSelectedRowMeasured,
                "The narrow back action keeps the same selected data and a measured selected native list row.");
            Assert("send-back-selection-accent", page.NativeReviewSelectionMarkersValid,
                "Only the selected native row exposes the measured three-by-sixteen accent marker.");
            window.NativeReviewFocusChrome();
            await SettleAsync();
            Assert("send-unfocused-selection-accent", page.NativeReviewSelectionMarkersValid,
                "Moving focus into the shell preserves the selected row accent and keeps other row markers collapsed.");
            var beforeOrder = page.ViewModel.FilteredItems.Select(item => item.Id).ToArray();
            page.NativeReviewSort(true);
            await SettleAsync();
            Assert("send-reordered-selection-accent", page.NativeReviewListOnly && page.NativeReviewSelectedId == retainedId
                && !page.ViewModel.FilteredItems.Select(item => item.Id).SequenceEqual(beforeOrder) && page.NativeReviewSelectionMarkersValid,
                "The actual production sort reorders the rows without losing or duplicating the selected accent.");
            page.NativeReviewSort(false);
            await SettleAsync();
            await CaptureAsync("send-narrow-list-after-back");
            var revised = fixture.ReviseSendForRefresh();
            await page.NativeReviewRefreshAsync();
            await SettleAsync();
            Assert("send-refresh-preserves-returned-list", page.NativeReviewListOnly && page.NativeReviewSelectedId == retainedId
                && page.NativeReviewSelectedRowMeasured,
                "A production refresh preserves the returned list pane and selected row.");
            await CaptureAsync("send-narrow-list-after-refresh");
            window.NativeReviewSetViewport(1280, 720, window.NativeReviewRoot.RequestedTheme);
            await SettleAsync();
            window.NativeReviewCorrectViewport(1280, 720);
            await SettleAsync();
            await WaitUntilAsync(() => page.NativeReviewDetailMatches(revised), "send-same-id-revision-detail-refreshed");
            Assert("send-same-id-revision-wide-detail", page.NativeReviewDetailVisible,
                "Widening the native window exposes the revised name, access count, revision and matching menu target.");
            await CaptureAsync("send-refreshed-detail-wide");
            window.NativeReviewSetViewport(720, 720, window.NativeReviewRoot.RequestedTheme);
            await SettleAsync();
            window.NativeReviewCorrectViewport(720, 720);
            await SettleAsync();
            Assert("send-revision-restores-narrow-list", page.NativeReviewListOnly && page.NativeReviewSelectedId == retainedId,
                "Returning to the narrow width keeps the revised selected record in the list pane.");
            Assert("send-same-item-click-enabled", page.NativeReviewItemClickEnabled,
                "The real list enables item-click dispatch for its retained selection.");
            await page.NativeReviewReopenSameItemAsync();
            await WaitUntilAsync(() => page.NativeReviewDetailOnly && page.NativeReviewSelectedId == retainedId,
                "send-same-item-click-path-reopens");
            await CaptureAsync("send-narrow-same-item-reopened");
            page.NativeReviewBackToList();
            await WaitUntilAsync(() => page.NativeReviewListOnly, "send-before-enter-returned-list");
            page.AcceptSearchResult();
            await WaitUntilAsync(() => page.NativeReviewDetailOnly && page.NativeReviewSelectedId == retainedId,
                "send-retained-item-enter-path-reopens");
            page.NativeReviewBackToList();
            await WaitUntilAsync(() => page.NativeReviewListOnly, "send-after-enter-returned-list");
            fixture.RestoreSendAfterRefresh();
            await page.NativeReviewRefreshAsync();
            await SettleAsync();
        }
        foreach (var type in new[] { SendType.Text, SendType.File })
        {
            page.NativeReviewSelect(type);
            await CaptureAsync($"send-detail-{type.ToString().ToLowerInvariant()}");
            await page.NativeReviewEditSelectedAsync();
            Assert($"send-editor-{type}-open", page.NativeReviewEditorOpen, "The production Send editor opened with a synthetic draft.");
            await CaptureAsync($"send-edit-{type.ToString().ToLowerInvariant()}");
            page.NativeReviewResetEditor();
            page.BeginCreate(type);
            await WaitUntilAsync(() => page.NativeReviewEditorOpen, $"send-new-{type}-editor-open");
            page.NativeReviewSetDraft(type == SendType.Text ? "Example launch notes" : "Example document", "Synthetic notes shared only inside this review fixture.");
            await CaptureAsync($"send-new-{type.ToString().ToLowerInvariant()}");
            page.NativeReviewResetEditor();
        }
        page.ApplySearch("missing-synthetic-send");
        window.NativeReviewRefreshShell();
        await SettleAsync();
        Assert("send-empty-search-content-visible", page.NativeReviewEmptyContentVisible && page.NativeReviewEmptySearchActionVisible,
            "The production search reconciliation presents a measured empty message and clear-search action.");
        Assert("send-empty-search-shell-query", window.NativeReviewSearchQuery == page.ViewModel.SearchQuery,
            "The title search and production Send page share the synthetic query.");
        if (window.NativeReviewRoot.ActualWidth < 800)
            Assert("send-empty-search-narrow-list-visible", page.NativeReviewListOnly,
                "A search with no results returns the narrow workspace to its list and empty actions.");
        await CaptureAsync("send-empty-search");
        page.ApplySearch(string.Empty);
        window.NativeReviewRefreshShell();
        fixture.SendEmpty = true;
        await page.NativeReviewRefreshAsync();
        window.NativeReviewRefreshShell();
        await SettleAsync();
        Assert("send-empty-service-state", page.ViewModel.LoadState == DataPageState.Empty && page.ViewModel.Items.Count == 0,
            "An empty synthetic service response uses the production empty-state projection.");
        Assert("send-empty-service-content-visible", page.NativeReviewEmptyContentVisible && page.NativeReviewEmptyCreateActionVisible,
            "The production load path presents a measured empty message and create action.");
        if (window.NativeReviewRoot.ActualWidth < 800)
            Assert("send-empty-service-narrow-list-visible", page.NativeReviewListOnly,
                "An empty service refresh clears the stale detail and exposes the narrow list.");
        await CaptureAsync("send-empty");
        fixture.SendEmpty = false;
        await page.NativeReviewRefreshAsync();
        await WaitUntilAsync(() => page.ViewModel.LoadState == DataPageState.Content, "send-list-restored");
        page.SelectFilter("send:receive");
        window.NativeReviewRefreshShell();
        await CaptureAsync("send-receive");
    }

    private async Task ReviewReportsAsync()
    {
        window.NativeReviewNavigate("reports");
        await WaitUntilAsync(() => window.NativeReviewFrame.Content is SecurityReportPage { IsLoaded: true }, "reports-page-loaded");
        var page = (SecurityReportPage)window.NativeReviewFrame.Content;
        await page.RefreshAsync();
        foreach (var category in options.EssentialOnly ? new[] { "all" } : new[] { "all", "reuse", "weak", "http", "breach" })
        {
            page.SelectCategory(category);
            window.NativeReviewRefreshShell();
            await CaptureAsync($"reports-{category}");
        }
        page.SelectCategory("reuse");
        var viewModel = page.ViewModel;
        var candidate = viewModel.IssueGroups.SelectMany(group => group.Items)
            .First(issue => issue.Category == "reuse" && issue.CipherId != fixture.DetailIds[VaultItemKind.Login]);
        var categoryCount = viewModel.GetCategoryIssueCount(candidate.Category);
        var ignoredCount = viewModel.IgnoredCount;
        viewModel.IgnoreIssueCommand.Execute(candidate);
        await WaitUntilAsync(() => viewModel.IgnoredCount == ignoredCount + 1 && viewModel.IsIssueIgnored(candidate.Category, candidate.CipherId),
            "report-ignore-committed");
        page.SelectCategory("ignored");
        window.NativeReviewRefreshShell();
        await SettleAsync();
        var ignoredRow = viewModel.IssueGroups.SelectMany(group => group.Items)
            .FirstOrDefault(issue => issue.Category == candidate.Category && issue.CipherId == candidate.CipherId && issue.IsIgnored);
        Assert("report-ignore-row-and-count", ignoredRow is not null && viewModel.GetCategoryIssueCount(candidate.Category) == categoryCount - 1,
            "The production ignore command moves the synthetic non-breach reminder into the ignored projection.");
        Assert("report-ignore-sidebar-count", window.NativeReviewNavigationCount("reports:ignored") == viewModel.IgnoredCount.ToString(),
            $"expected={viewModel.IgnoredCount}; native={window.NativeReviewNavigationCount("reports:ignored")}");
        if (window.NativeReviewRoot.ActualWidth >= 1200)
            Assert("report-ignore-sidebar-count-visible", window.NativeReviewNavigationCountVisible("reports:ignored"),
                "The actual expanded sidebar count is measured inside its navigation item.");
        await CaptureAsync("reports-ignored");
        viewModel.RestoreIssueCommand.Execute(ignoredRow);
        await WaitUntilAsync(() => viewModel.IgnoredCount == ignoredCount && !viewModel.IsIssueIgnored(candidate.Category, candidate.CipherId),
            "report-restore-committed");
        page.SelectCategory(candidate.Category);
        window.NativeReviewRefreshShell();
        await SettleAsync();
        Assert("report-restore-row-and-count", viewModel.GetCategoryIssueCount(candidate.Category) == categoryCount
            && viewModel.IssueGroups.SelectMany(group => group.Items).Any(issue => issue.CipherId == candidate.CipherId && !issue.IsIgnored),
            "The production restore command returns the reminder to its original category.");
        Assert("report-restore-sidebar-count", window.NativeReviewNavigationCount("reports:" + candidate.Category) == categoryCount.ToString(),
            $"expected={categoryCount}; native={window.NativeReviewNavigationCount("reports:" + candidate.Category)}");
        await CaptureAsync("reports-restored");
        page.SelectCategory("done");
        window.NativeReviewRefreshShell();
        Assert("report-done-not-inflated-by-ignore", viewModel.ResolvedCount == 0 && viewModel.ResolvedItems.Count == 0,
            "Ignoring and restoring a reminder does not mark its vault item as fixed.");
        await CaptureAsync("reports-done-empty");
        page.SelectCategory("all");
        window.NativeReviewRefreshShell();
    }

    private async Task ReviewSettingsAsync()
    {
        window.NativeReviewNavigate("settings");
        await WaitUntilAsync(() => window.NativeReviewFrame.Content is SettingsPage { IsLoaded: true }, "settings-page-loaded");
        var page = (SettingsPage)window.NativeReviewFrame.Content;
        foreach (var section in options.EssentialOnly ? new[] { "account", "security", "appearance" } : new[] { "account", "security", "appearance", "ssh", "devices", "import", "about" })
        {
            page.NativeReviewSelectSection(section);
            window.NativeReviewRefreshShell();
            await CaptureAsync($"settings-{section}");
            if (section == "import")
                await ReviewImportPresentationAsync(page);
        }
        page.NativeReviewShowTwoFactor();
        window.NativeReviewRefreshShell();
        await CaptureAsync("settings-two-factor");
        page.NativeReviewShowPassword();
        window.NativeReviewRefreshShell();
        await CaptureAsync("settings-master-password");
        page.NativeReviewShowAccountManagement();
        await WaitUntilAsync(() => page.NativeReviewAccountManagement is { IsLoaded: true }, "account-management-loaded");
        var management = page.NativeReviewAccountManagement!;
        Assert("account-management-synthetic-account", management.ViewModel.CurrentEmail == "reviewer@review.example",
            "The real management view is bound to the fixture account and a rejecting in-memory management service.");
        Assert("account-management-inputs-measured", management.NativeReviewFieldsMeasured,
            "The native account-management token, address and password controls have usable measured widths.");
        Assert("account-management-default-security-state", management.NativeReviewInitialSecurityState,
            "Email changes require a new challenge; code confirmation is disabled and both password inputs begin empty.");
        window.NativeReviewRefreshShell();
        await CaptureAsync("settings-account-management");
    }

    private async Task ReviewImportPresentationAsync(SettingsPage settings)
    {
        await WaitUntilAsync(() => settings.NativeReviewImportExport is { IsLoaded: true }, "import-export-page-loaded");
        var page = settings.NativeReviewImportExport!;
        var format = Descendants<ComboBox>(page).Single(combo =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(combo) == "ImportFormatCombo");
        var destination = Descendants<ComboBox>(page).Single(combo =>
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(combo) == "ImportDestinationCombo");
        Assert("import-eight-format-options", format.Items.Count == 8
            && format.Items.OfType<ComboBoxItem>().All(item => item.Content is string label && !string.IsNullOrWhiteSpace(label)),
            "The native import selector exposes eight nonempty, localized format labels.");
        for (var index = 0; index < format.Items.Count; index++)
        {
            format.SelectedIndex = index;
            await SettleAsync();
            Assert($"import-format-index-{index}-maps-to-contract", (int)page.ViewModel.SelectedImportFormat == index,
                $"nativeIndex={format.SelectedIndex}; viewModel={page.ViewModel.SelectedImportFormat}");
        }
        format.IsDropDownOpen = true;
        try { await CaptureAsync("settings-import-eight-formats"); }
        finally { format.IsDropDownOpen = false; }
        await SettleAsync();
        Assert("import-destination-options-bind", destination.Items.Count == fixture.Session.Folders.Count + 1
            && page.ViewModel.DestinationFolders.Count == destination.Items.Count,
            "The target-folder selector receives only synthetic personal folders plus preserve-source-folders.");
        for (var index = 0; index < destination.Items.Count; index++)
        {
            destination.SelectedIndex = index;
            await SettleAsync();
            var expected = page.ViewModel.DestinationFolders[index];
            Assert($"import-destination-{index}-renders-typed-label", destination.SelectedItem is ImportFolderOption selected
                && selected.Id == expected.Id && page.ViewModel.SelectedDestinationFolderId == expected.Id
                && Descendants<TextBlock>(destination).Any(text => text.Text == expected.DisplayName && text.ActualWidth > 0),
                $"index={index}; expectedId={expected.Id ?? "preserve"}; expectedLabel={expected.DisplayName}");
        }
        destination.IsDropDownOpen = true;
        try { await CaptureAsync("settings-import-target-folder"); }
        finally { destination.IsDropDownOpen = false; }
        destination.SelectedIndex = 0;
        format.SelectedIndex = 0;
        await SettleAsync();
    }

    private async Task ReviewAuthenticationAsync()
    {
        window.NativeReviewShowAuth(registration: false);
        await WaitUntilAsync(() => window.NativeReviewLoginFrame.Content is LoginPage { IsLoaded: true }, "auth-login-page-loaded");
        var login = (LoginPage)window.NativeReviewLoginFrame.Content;
        login.ViewModel.Email = "review@example.invalid";
        if (window.NativeReviewRoot.ActualWidth < 800)
        {
            var originalWidth = (int)Math.Round(window.NativeReviewRoot.ActualWidth);
            var originalHeight = (int)Math.Round(window.NativeReviewRoot.ActualHeight);
            var theme = window.NativeReviewRoot.RequestedTheme;
            window.NativeReviewSetViewport(1280, originalHeight, theme);
            await SettleAsync();
            window.NativeReviewCorrectViewport(1280, originalHeight);
            await SettleAsync();
            Assert("auth-collapsed-nav-wide-resize", window.NativeReviewIsAuth && window.NativeReviewNavigationCollapsed
                && Math.Abs(window.NativeReviewRoot.ActualWidth - 1280) < 1
                && ReferenceEquals(window.NativeReviewLoginFrame.Content, login),
                $"actualWidth={window.NativeReviewRoot.ActualWidth:F2}; auth={window.NativeReviewIsAuth}; navigationCollapsed={window.NativeReviewNavigationCollapsed}");
            window.NativeReviewSetViewport(originalWidth, originalHeight, theme);
            await SettleAsync();
            window.NativeReviewCorrectViewport(originalWidth, originalHeight);
            await SettleAsync();
            Assert("auth-collapsed-nav-resize-restored", window.NativeReviewIsAuth && window.NativeReviewNavigationCollapsed
                && Math.Abs(window.NativeReviewRoot.ActualWidth - originalWidth) < 1,
                $"restoredWidth={window.NativeReviewRoot.ActualWidth:F2}; expected={originalWidth}");
        }
        await CaptureAsync("auth-account");
        await login.NativeReviewSubmitAsync();
        await WaitUntilAsync(() => login.ViewModel.IsPasswordStage && login.ViewModel.ShowLoginWithDeviceOption,
            "auth-device-login-availability-probed");
        await SettleAsync();
        Assert("auth-device-login-action-visible", login.NativeReviewDeviceLoginActionVisible,
            "The production account continuation and in-memory availability/quarantine services expose a measured device-login action.");
        Assert("auth-device-login-icon-measured", login.NativeReviewDeviceLoginIconMeasured && login.NativeReviewDeviceLoginActionNamed,
            "The native device-login action measures its 16-pixel phone/laptop icon and retains an explicit accessible name.");
        await CaptureAsync("auth-password");
        window.NativeReviewShowAuth(registration: true);
        await WaitUntilAsync(() => window.NativeReviewLoginFrame.Content is RegisterPage { IsLoaded: true }, "auth-register-page-loaded");
        var register = (RegisterPage)window.NativeReviewLoginFrame.Content;
        register.ViewModel.Email = "review@example.invalid";
        await CaptureAsync("auth-register");
    }

    private async Task CaptureAsync(string name)
    {
        _case = name;
        await SettleAsync();
        var directory = Path.Combine(options.OutputDirectory, _theme, _viewport);
        Directory.CreateDirectory(directory);
        var root = window.NativeReviewRoot;
        if (window.NativeReviewFrame.Content is SendPage sendPage)
        {
            if (sendPage.NativeReviewRowsVisible)
                Assert("send-visible-row-selection-accents", sendPage.NativeReviewSelectionMarkersValid,
                    $"scale={root.XamlRoot.RasterizationScale}; {sendPage.NativeReviewMarkerMeasurements}");
            if (name.StartsWith("send-new-", StringComparison.Ordinal))
            {
                Assert("send-new-draft-no-double-accent", sendPage.NativeReviewDraftHasNoListMarker,
                    "Opening a new draft hides every realized real-row selection accent.");
                if (root.ActualWidth >= 800)
                    Assert("send-new-draft-accent-measured", sendPage.NativeReviewDraftMarkerMeasured,
                        "The visible draft alone retains its measured 3-by-16 accent.");
            }
        }
        if (window.NativeReviewFrame.Content is VaultPage vaultPage
            && fixture.Vault.Detail is LoginDetail && !fixture.Vault.IsEditing)
            ObserveWebsiteFields(name, vaultPage);
        if (name == "settings-master-password")
        {
            var input = Descendants<PasswordBox>(root).FirstOrDefault(element => element.Name == "CurrentPasswordBox");
            Assert("master-password-form-width", input is { IsLoaded: true, ActualWidth: >= 200 },
                $"actualWidth={input?.ActualWidth ?? 0:F2}");
        }
        var primaryPath = Path.Combine(directory, name + ".png");
        var native = await RenderAsync(root, primaryPath, requireVariation: true);
        var layers = new List<ReviewImage>();
        var nativeWindows = new List<ReviewOwnWindowImage>();
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Where(popup => popup.IsOpen && popup.Child is FrameworkElement))
        {
            var child = (FrameworkElement)popup.Child;
            if (!child.IsLoaded)
            {
                RecordPopupSkip(child, "The popup child was already unloaded before capture.", null);
                continue;
            }
            if (child.ActualWidth <= 0 || child.ActualHeight <= 0) continue;
            var popupPath = Path.Combine(directory, $"{name}.native-popup-{layers.Count}.png");
            var descendants = Descendants<FrameworkElement>(child).ToArray();
            var isScrim = !descendants.Any(element => element is Control or TextBlock);
            if (isScrim)
            {
                RecordPopupSkip(child, "The pure native scrim contains no controls or text.", null);
                continue;
            }
            ReviewImage popupImage;
            try
            {
                popupImage = await RenderAsync(child, popupPath, requireVariation: false);
            }
            catch (ReviewRasterUnavailableException)
            {
                await Task.Delay(35);
                if (!popup.IsOpen || !ReferenceEquals(popup.Child, child) || !child.IsLoaded)
                {
                    RecordPopupSkip(child, "The popup retired while its native XAML raster was being requested.", null);
                    continue;
                }
                // Some external PopupWindowSiteBridge visuals produce no XAML pixel buffer.
                // Preserve an actual, PID-checked native window layer rather than inventing pixels.
                var captures = await OwnProcessNativeCapture.CaptureVisibleAsync(directory, name);
                var scale = root.XamlRoot.RasterizationScale;
                var expectedWidth = child.ActualWidth * scale;
                var expectedHeight = child.ActualHeight * scale;
                var capturedPopup = captures.Any(capture => capture.HasVariation
                    && Math.Abs(capture.Width - expectedWidth) < 40
                    && Math.Abs(capture.Height - expectedHeight) < 40);
                Assert("native-popup-empty-raster-has-own-window-evidence", capturedPopup,
                    $"type={child.GetType().Name}; ownWindowLayers={captures.Count}; expectedPixels={expectedWidth:F0}x{expectedHeight:F0}");
                if (!capturedPopup)
                    throw;
                foreach (var capture in captures)
                    nativeWindows.Add(new ReviewOwnWindowImage(Path.GetRelativePath(options.OutputDirectory, capture.Path).Replace('\\', '/'),
                        capture.Width, capture.Height, capture.WindowClass, capture.Sha256, capture.HasVariation));
                RecordPopupSkip(child, "The active popup's empty XAML raster was replaced by retained, matching own-process native window evidence.", null);
                continue;
            }
            catch (COMException exception)
            {
                // A native close animation can detach a popup while RenderAsync yields.
                // Keep active menu/dialog failures fatal; skip only demonstrably retired objects.
                await Task.Delay(35);
                if (!popup.IsOpen || !ReferenceEquals(popup.Child, child) || !child.IsLoaded)
                {
                    RecordPopupSkip(child, "The popup closed, replaced its child, or unloaded during the render await.", exception.HResult);
                    continue;
                }
                throw;
            }
            layers.Add(popupImage);
            if (!isScrim && !popupImage.HasVariation)
            {
                var captures = await OwnProcessNativeCapture.CaptureVisibleAsync(directory, name);
                foreach (var capture in captures)
                    nativeWindows.Add(new ReviewOwnWindowImage(Path.GetRelativePath(options.OutputDirectory, capture.Path).Replace('\\', '/'),
                        capture.Width, capture.Height, capture.WindowClass, capture.Sha256, capture.HasVariation));
                var capturedPopup = captures.Any(capture => capture.HasVariation
                    && Math.Abs(capture.Width - popupImage.Width) < 40 && Math.Abs(capture.Height - popupImage.Height) < 40);
                Assert("native-popup-content-captured", capturedPopup,
                    $"type={child.GetType().Name}; ownWindowLayers={captures.Count}; expectedPixels={popupImage.Width}x{popupImage.Height}");
            }
            File.WriteAllText(Path.ChangeExtension(popupPath, ".layout.json"),
                JsonSerializer.Serialize(descendants.Where(element => element.ActualWidth > 0 && element.ActualHeight > 0)
                    .Select(element => new ReviewPopupNode(element.GetType().Name, element.Name, element.ActualWidth, element.ActualHeight, element.Opacity)).ToList(),
                    ReviewJsonContext.Default.ListReviewPopupNode));
        }
        var layout = Descendants<FrameworkElement>(root)
            .Where(element => !string.IsNullOrEmpty(element.Name) && element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0)
            .Select(element => Bounds(element, root)).ToList();
        _manifest.Captures.Add(new ReviewCapture(name, _theme, _viewport,
            root.ActualWidth, root.ActualHeight, root.XamlRoot.RasterizationScale,
            window.NativeReviewIsAuth ? window.NativeReviewLoginFrame.Content?.GetType().Name ?? "none" : window.NativeReviewFrame.Content?.GetType().Name ?? "none",
            native, layers, nativeWindows, layout));
        CheckShellLayout(root);
        SaveManifest("manifest.in-progress.json");
        File.WriteAllText(Path.Combine(options.OutputDirectory, "progress.txt"), $"{_manifest.Captures.Count} {_theme} {_viewport} {name}{Environment.NewLine}");
    }

    private void CheckShellLayout(FrameworkElement root)
    {
        if (root.FindName("AppTitleBar") is FrameworkElement title)
            Assert("titlebar-height", Math.Abs(title.ActualHeight - 48) < 0.1, $"actual={title.ActualHeight:F2}");
        if (!window.NativeReviewIsAuth && root.FindName("StatusBar") is FrameworkElement status)
            Assert("statusbar-height", Math.Abs(status.ActualHeight - 28) < 0.1, $"actual={status.ActualHeight:F2}");
        foreach (var elementName in new[] { "TopTabs", "TitleSearchHost", "TitleActions", "StatusBar" })
        {
            if (root.FindName(elementName) is not FrameworkElement { Visibility: Visibility.Visible } element || element.ActualWidth <= 0) continue;
            var bounds = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            Assert($"{elementName}-inside-root", bounds.X >= -1 && bounds.Y >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1,
                $"x={bounds.X:F2}; y={bounds.Y:F2}; width={bounds.Width:F2}; height={bounds.Height:F2}");
        }
        var tabs = root.FindName("TopTabs") as FrameworkElement;
        var search = root.FindName("TitleSearchHost") as FrameworkElement;
        var actions = root.FindName("TitleActions") as FrameworkElement;
        void CheckHorizontalOrder(FrameworkElement? left, FrameworkElement? right, string name)
        {
            if (left is not { Visibility: Visibility.Visible, ActualWidth: > 0 }
                || right is not { Visibility: Visibility.Visible, ActualWidth: > 0 })
                return;
            var leftBounds = left.TransformToVisual(root).TransformBounds(
                new Windows.Foundation.Rect(0, 0, left.ActualWidth, left.ActualHeight));
            var rightBounds = right.TransformToVisual(root).TransformBounds(
                new Windows.Foundation.Rect(0, 0, right.ActualWidth, right.ActualHeight));
            Assert(name, leftBounds.Right <= rightBounds.Left + 1,
                $"leftRight={leftBounds.Right:F2}; rightLeft={rightBounds.Left:F2}; language={options.LanguagePreference}");
        }
        CheckHorizontalOrder(tabs, search, "title-tabs-do-not-overlap-search");
        CheckHorizontalOrder(search, actions, "title-search-does-not-overlap-actions");
        if (search?.Visibility != Visibility.Visible)
            CheckHorizontalOrder(tabs, actions, "title-tabs-do-not-overlap-actions");
    }

    private async Task<ReviewImage> RenderAsync(FrameworkElement element, string path, bool requireVariation)
    {
        var bitmap = new RenderTargetBitmap();
        var targetWidth = Math.Max(1, (int)Math.Round(element.ActualWidth));
        var targetHeight = Math.Max(1, (int)Math.Round(element.ActualHeight));
        await bitmap.RenderAsync(element, targetWidth, targetHeight);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0
            || (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 != pixels.LongLength)
            throw new ReviewRasterUnavailableException(
                $"Native raster has invalid dimensions or pixels: {bitmap.PixelWidth}x{bitmap.PixelHeight}; bytes={pixels.LongLength}.");
        using (var stream = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        var hasVariation = false;
        for (var index = 4; index + 3 < pixels.Length; index += 4)
        {
            if (pixels[index] != pixels[0] || pixels[index + 1] != pixels[1] || pixels[index + 2] != pixels[2])
            {
                hasVariation = true;
                break;
            }
        }
        if (requireVariation)
            Assert("native-raster-not-blank", hasVariation, $"type={element.GetType().Name}; width={bitmap.PixelWidth}; height={bitmap.PixelHeight}");
        return new ReviewImage(Path.GetRelativePath(options.OutputDirectory, path).Replace('\\', '/'), bitmap.PixelWidth, bitmap.PixelHeight, ReviewOptions.HashFile(path), hasVariation);
    }

    private static ReviewBounds Bounds(FrameworkElement element, FrameworkElement root)
    {
        try
        {
            var bounds = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return new ReviewBounds(element.Name, element.GetType().Name, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        catch (ArgumentException)
        {
            return new ReviewBounds(element.Name, element.GetType().Name, 0, 0, element.ActualWidth, element.ActualHeight);
        }
    }

    private List<ContentDialog> OpenDialogs() => VisualTreeHelper.GetOpenPopupsForXamlRoot(window.NativeReviewRoot.XamlRoot)
        .Where(popup => popup.IsOpen && popup.Child is not null)
        .SelectMany(popup => Descendants<ContentDialog>(popup.Child)).Distinct().ToList();

    private sealed class ReviewRasterUnavailableException(string message) : InvalidOperationException(message);

    private bool HasOpenPopups() => VisualTreeHelper.GetOpenPopupsForXamlRoot(window.NativeReviewRoot.XamlRoot)
        .Any(popup => popup.IsOpen);

    private void RecordPopupSkip(FrameworkElement child, string reason, int? hresult) =>
        _manifest.PopupSkips.Add(new ReviewPopupSkip(_case, _theme, _viewport, child.GetType().Name,
            hresult is { } value ? $"0x{value:X8}" : null, reason));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private async Task WaitUntilAsync(Func<bool> condition, string name)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < TimeSpan.FromSeconds(8))
            await Task.Delay(25);
        var passed = condition();
        Assert(name, passed, passed ? "The native state completed." : "The native state did not complete before the bounded timeout.");
        if (!passed) throw new InvalidOperationException(name);
    }

    private async Task SettleAsync()
    {
        // Yield past navigation and allow the native dispatcher/compositor to finish its pass.
        // The captured raster and measured bounds verify layout without forcing it synchronously.
        await Task.Delay(120);
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => idle.TrySetResult()))
            throw new InvalidOperationException("The synthetic review dispatcher is unavailable.");
        await idle.Task;
        await Task.Delay(35);
    }

    private void Assert(string name, bool passed, string detail)
    {
        _manifest.Assertions.Add(new ReviewAssertion(_case, _theme, _viewport, name, passed, detail));
        if (!passed) SaveManifest("manifest.in-progress.json");
    }

    private void SaveManifest(string name) => File.WriteAllText(Path.Combine(options.OutputDirectory, name),
        JsonSerializer.Serialize(_manifest, ReviewJsonContext.Default.ReviewManifest));
}

internal sealed class ReviewManifest
{
    public string Schema { get; set; } = "winvaultwarden-native-ui-review-v1";
    public string Renderer { get; set; } = "Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap";
    public string Provenance { get; set; } = "Production App.xaml, MainWindow.xaml and page XAML/XBF, driven by an explicitly opted-in Debug-only entry point.";
    public string Backdrop { get; set; } = "Production WardenMicaBrush fallback; system-composed Mica and caption buttons are outside the XAML raster.";
    public string AnimationPolicy { get; set; } = "Fixture Frames suppress navigation transitions to capture stable production page content; production transition settings are unchanged.";
    public string PopupPolicy { get; set; } = "Open native popup visual trees are exported separately. A WinUI PopupWindowSiteBridge that RenderTargetBitmap cannot rasterize is captured by PrintWindow only after current-process ownership checks. Flyout opening animations are disabled for deterministic capture.";
    public bool SyntheticOnly { get; set; } = true;
    public bool NormalStartupUsed { get; set; }
    public bool ExternalInputAutomationUsed { get; set; }
    public bool RealClipboardUsed { get; set; }
    public bool PersistentPreferencesUsed { get; set; }
    public string FixtureLanguage { get; set; } = "zh-CN";
    public string FixtureDataMode { get; set; } = "full";
    public bool ProcessLocalMrtCoreUsed { get; set; } = true;
    public bool SyntheticPlatformServicesUsed { get; set; } = true;
    public bool FixtureDeviceLoginAvailable { get; set; } = true;
    public bool FixtureSsoAuthorizationAvailable { get; set; }
    public string Round { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int ProcessId { get; set; }
    public string AssemblyMvid { get; set; } = string.Empty;
    public string AssemblySha256 { get; set; } = string.Empty;
    public string BinaryPathKind { get; set; } = string.Empty;
    public bool DynamicCodeSupported { get; set; }
    public bool DynamicCodeCompiled { get; set; }
    public string WindowsAppSdkVersion { get; set; } = string.Empty;
    public string RunProfile { get; set; } = string.Empty;
    public int ExpectedMatrixCells { get; set; }
    public List<string> CompletedMatrixCells { get; set; } = [];
    public bool Completed { get; set; }
    public bool Passed { get; set; }
    public List<ReviewSourceFile> SourceFiles { get; set; } = [];
    public List<ReviewCapture> Captures { get; set; } = [];
    public List<ReviewAssertion> Assertions { get; set; } = [];
    public List<ReviewWebsiteObservation> WebsiteObservations { get; set; } = [];
    public List<ReviewPopupSkip> PopupSkips { get; set; } = [];
}

internal sealed record ReviewSourceFile(string Path, string Sha256);
internal sealed record ReviewImage(string Path, int Width, int Height, string Sha256, bool HasVariation);
internal sealed record ReviewOwnWindowImage(string Path, int Width, int Height, string WindowClass, string Sha256, bool HasVariation);
internal sealed record ReviewBounds(string Name, string Type, double X, double Y, double Width, double Height);
internal sealed record ReviewCapture(string Name, string Theme, string Viewport, double WidthDip, double HeightDip,
    double RasterizationScale, string Page, ReviewImage Image, List<ReviewImage> PopupLayers, List<ReviewOwnWindowImage> OwnWindowLayers, List<ReviewBounds> NamedLayout);
internal sealed record ReviewAssertion(string Case, string Theme, string Viewport, string Name, bool Passed, string Detail);
internal sealed record ReviewPopupNode(string Type, string Name, double Width, double Height, double Opacity);
internal sealed record ReviewWebsiteObservation(string Stage, string Theme, string Viewport, string OriginalSyntheticUri,
    string ExpectedDisplay, string NativeText, string InlineText);
internal sealed record ReviewPopupSkip(string Case, string Theme, string Viewport, string Type, string? HResult, string Reason);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReviewManifest))]
[JsonSerializable(typeof(List<ReviewPopupNode>))]
internal sealed partial class ReviewJsonContext : JsonSerializerContext;
