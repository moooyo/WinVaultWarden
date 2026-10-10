using System.Runtime.InteropServices;
using App.Services;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace App.Controls;

public sealed class CopyStripCopiedEventArgs(string itemId, string fieldKey, string label) : EventArgs
{
    public string ItemId { get; } = itemId;
    public string FieldKey { get; } = fieldKey;
    public string Label { get; } = label;
}

/// <summary>Shows actual vault values in their paste order without bypassing item authorization.</summary>
public sealed partial class CopyStrip : UserControl
{
    private readonly List<Cell> _cells = [];
    private readonly HashSet<string> _revealed = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _feedbackTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private IClipboardStatus? _clipboardStatus;
    private string? _nextField;
    private string? _copiedField;
    private long _copiedVersion;
    private long _contextVersion;
    private bool _busy;
    private CancellationTokenSource? _operationCancellation;

    private sealed class Cell(string key, string label, bool secret = false, bool mono = false, bool add = false)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public bool Secret { get; } = secret;
        public bool Mono { get; } = mono;
        public bool Add { get; } = add;
        public Grid Root { get; } = new() { MinHeight = 92 };
        public Button Button { get; } = new();
        public TextBlock LabelText { get; } = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        public TextBlock ValueText { get; } = new() { FontSize = 20, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, LineHeight = 32 };
        public FontIcon CopyIcon { get; } = new() { Glyph = "\uE8C8", FontSize = 14, Opacity = 0 };
        public FontIcon DoneIcon { get; } = new() { Glyph = "\uE73E", FontSize = 12, Visibility = Visibility.Collapsed };
        public Button Reveal { get; } = new() { Width = 32, Height = 32, Visibility = Visibility.Collapsed };
        public FontIcon RevealIcon { get; } = new() { Glyph = "\uE890", FontSize = 16 };
        public ProgressBar Progress { get; } = new() { Minimum = 0, Maximum = 100, Height = 2, MinHeight = 2, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        public Border Flash { get; } = new() { CornerRadius = new CornerRadius(6), Opacity = 0, IsHitTestVisible = false };
        public TotpField? Totp { get; set; }
        public StackPanel HeaderCountdown { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
        public ProgressRing HeaderRing { get; } = new() { Width = 16, Height = 16, MinWidth = 16, MinHeight = 16, Minimum = 0, Maximum = 30, IsIndeterminate = false };
        public TextBlock HeaderSeconds { get; } = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        public bool Hovered { get; set; }
    }

    public CopyStrip()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateFeedback();
        _feedbackTimer.Tick += (_, _) => ClearMessage();
        Loaded += (_, _) =>
        {
            _clipboardStatus = global::App.App.Services?.GetService<IClipboardService>() as IClipboardStatus;
            if (_clipboardStatus is not null) _clipboardStatus.Changed += OnClipboardChanged;
            BuildCells();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _contextVersion++;
            _operationCancellation?.Cancel();
            _timer.Stop();
            if (_clipboardStatus is not null) _clipboardStatus.Changed -= OnClipboardChanged;
            _clipboardStatus = null;
            ClearVisualValues();
            ResetRelay();
            ClearMessage();
        };
        ActualThemeChanged += (_, _) => { RenderValues(); UpdateFeedback(); };
    }

    public CipherDetail? Detail { get => (CipherDetail?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(CipherDetail), typeof(CopyStrip), new PropertyMetadata(null, (d, e) => ((CopyStrip)d).OnDetailChanged((CipherDetail?)e.OldValue)));
    public bool ShowAddTotp { get => (bool)GetValue(ShowAddTotpProperty); set => SetValue(ShowAddTotpProperty, value); }
    public static readonly DependencyProperty ShowAddTotpProperty = DependencyProperty.Register(
        nameof(ShowAddTotp), typeof(bool), typeof(CopyStrip), new PropertyMetadata(false, (d, _) => ((CopyStrip)d).BuildCells()));

    /// <summary>The page retains the item reprompt gate and verifies every reveal before returning true.</summary>
    public Func<string, bool, CancellationToken, Task<bool>>? AuthorizeSensitiveActionAsync { get; set; }
    public Func<CipherDetail?>? ReadAuthorizedDetail { get; set; }
    public event EventHandler<CopyStripCopiedEventArgs>? Copied;
    public event EventHandler? AddTotpRequested;

    public Task CopyPrimaryAsync()
    {
        var field = _cells.FirstOrDefault(cell => cell.Key == "password")
            ?? _cells.FirstOrDefault(cell => !cell.Add);
        return field is null ? Task.CompletedTask : CopyAsync(field.Key);
    }

    public void BeginRelay()
    {
        _nextField = _cells.FirstOrDefault(cell => !cell.Add)?.Key;
        UpdateFeedback();
    }

    public void ResetRelay()
    {
        _nextField = null;
        _copiedField = null;
        _copiedVersion = 0;
        _revealed.Clear();
        RenderValues();
        UpdateFeedback();
    }

    private void OnDetailChanged(CipherDetail? previous)
    {
        _revealed.Clear();
        ClearVisualValues();
        if (!string.Equals(previous?.Id, Detail?.Id, StringComparison.Ordinal))
        {
            _contextVersion++;
            _operationCancellation?.Cancel();
            _nextField = _copiedField = null;
            _copiedVersion = 0;
        }
        BuildCells();
    }

    private void BuildCells()
    {
        if (CellsGrid is null) return;
        ClearVisualValues();
        _cells.Clear();
        CellsGrid.Children.Clear();
        CellsGrid.ColumnDefinitions.Clear();
        if (Detail is not { } detail) { Visibility = Visibility.Collapsed; return; }
        void Add(string key, string label, bool secret = false, bool mono = false)
        {
            if (!string.IsNullOrEmpty(ValueOf(detail, key)) || secret && !detail.SensitiveUnlocked)
                _cells.Add(new Cell(key, label, secret, mono));
        }
        switch (detail)
        {
            case LoginDetail:
                Add("username", "用户名");
                Add("password", "密码", secret: true);
                Add("totp", "验证码", secret: true);
                if (ShowAddTotp && detail.SensitiveUnlocked && string.IsNullOrEmpty(ValueOf(detail, "totp")) && !detail.IsDeleted)
                    _cells.Add(new Cell("totp", "两步验证", add: true));
                break;
            case CardDetail:
                Add("number", "卡号", secret: true);
                Add("expiry", "有效期");
                Add("cvv", "安全码", secret: true);
                break;
            case IdentityDetail:
                Add("fullName", "姓名");
                Add("phone", "手机");
                Add("email", "邮箱");
                break;
            case SshDetail:
                Add("publicKey", "公钥", mono: true);
                Add("fingerprint", "指纹", mono: true);
                Add("privateKey", "私钥", secret: true);
                break;
        }
        Visibility = _cells.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            CellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(cell.Root, i);
            cell.Button.Style = (Style)Resources["CopyStripCellStyle"];
            cell.Button.IsEnabled = !detail.IsDeleted;
            cell.Button.Click += async (_, _) => await CopyAsync(cell.Key);
            cell.Root.Children.Add(cell.Button);
            cell.Root.PointerEntered += (_, _) => { cell.Hovered = true; UpdateFeedback(); };
            cell.Root.PointerExited += (_, _) => { cell.Hovered = false; UpdateFeedback(); };
            cell.Root.Children.Add(cell.Flash);
            var content = new Grid { Margin = new Thickness(12, 12, 12, 20), IsHitTestVisible = false };
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            content.RowSpacing = 6;
            var labelRow = new Grid();
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            labelRow.Children.Add(cell.DoneIcon);
            Grid.SetColumn(cell.LabelText, 1);
            labelRow.Children.Add(cell.LabelText);
            Grid.SetColumn(cell.CopyIcon, 2);
            cell.CopyIcon.Margin = new Thickness(6, 0, 0, 0);
            labelRow.Children.Add(cell.CopyIcon);
            Grid.SetColumn(cell.HeaderCountdown, 3);
            cell.HeaderCountdown.Margin = new Thickness(6, 0, 0, 0);
            cell.HeaderCountdown.Children.Add(cell.HeaderRing);
            cell.HeaderCountdown.Children.Add(cell.HeaderSeconds);
            labelRow.Children.Add(cell.HeaderCountdown);
            content.Children.Add(labelRow);
            var valueRow = new Grid { ColumnSpacing = 8 };
            valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (cell.Key == "totp" && !cell.Add)
            {
                cell.Totp = new TotpField { IsCompact = true, IsReadOnly = true };
                valueRow.Children.Add(cell.Totp);
                cell.Root.SizeChanged += (_, _) => UpdateFeedback();
            }
            else valueRow.Children.Add(cell.ValueText);
            if (cell.Secret && cell.Key != "totp" && !detail.IsDeleted)
            {
                cell.ValueText.Margin = new Thickness(0, 0, 32, 0);
                cell.Reveal.Style = (Style)Application.Current.Resources["IconButtonStyle"];
                cell.Reveal.Content = cell.RevealIcon;
                cell.Reveal.HorizontalAlignment = HorizontalAlignment.Right;
                cell.Reveal.VerticalAlignment = VerticalAlignment.Bottom;
                cell.Reveal.Margin = new Thickness(0, 0, 12, 20);
                cell.Reveal.Visibility = Visibility.Visible;
                cell.Reveal.Click += async (_, _) => await RevealAsync(cell.Key);
            }
            Grid.SetRow(valueRow, 1);
            content.Children.Add(valueRow);
            cell.Root.Children.Add(content);
            cell.Root.Children.Add(cell.Reveal);
            cell.Progress.Margin = new Thickness(12, 0, 12, 8);
            cell.Progress.VerticalAlignment = VerticalAlignment.Bottom;
            cell.Root.Children.Add(cell.Progress);
            CellsGrid.Children.Add(cell.Root);
            AutomationProperties.SetName(cell.Button, cell.Add ? "添加验证码" : $"复制{cell.Label}");
            ToolTipService.SetToolTip(cell.Button, cell.Add ? "编辑此条目并保存验证码" : $"点击复制{cell.Label}" + (cell.Key == "password" ? " · Ctrl+C" : string.Empty));
        }
        RenderValues();
        UpdateFeedback();
    }

    private void RenderValues()
    {
        if (Detail is not { } detail) return;
        var monospace = (FontFamily)Application.Current.Resources["MonospaceFontFamily"];
        foreach (var cell in _cells)
        {
            var value = IsLoaded ? ValueOf(detail, cell.Key) : null;
            if (cell.Totp is not null)
            {
                cell.Totp.Secret = value ?? string.Empty;
                var valid = !detail.SensitiveUnlocked
                    || Crypto.TotpGenerator.TryGenerate(value, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out _);
                cell.Button.IsEnabled = !detail.IsDeleted && valid;
                ToolTipService.SetToolTip(cell.Button, valid ? "点击复制验证码" : "验证码密钥无效，编辑此条目以修正");
                continue;
            }
            var revealed = _revealed.Contains(cell.Key) && IsLoaded;
            cell.ValueText.Inlines.Clear();
            cell.ValueText.CharacterSpacing = cell.Secret ? revealed ? 20 : 120 : 0;
            cell.ValueText.FontSize = cell.Add ? 15 : cell.Secret && revealed ? 18 : cell.Mono ? 15 : 20;
            cell.ValueText.FontWeight = cell.Add ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            cell.ValueText.FontFamily = cell.Mono || cell.Secret && revealed ? monospace : FontFamily;
            cell.ValueText.Foreground = BrushFor(cell.Add ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
            cell.ValueText.Text = cell.Add ? "+ 添加验证码" : string.IsNullOrEmpty(value)
                ? cell.Secret && !detail.SensitiveUnlocked ? "••••••••••••" : "未设置"
                : cell.Secret && !revealed ? Mask(cell.Key, value) : value;
            if (revealed && cell.Key == "password" && value is not null)
            {
                cell.ValueText.Text = string.Empty;
                foreach (var character in value)
                    cell.ValueText.Inlines.Add(new Run
                    {
                        Text = character.ToString(),
                        Foreground = BrushFor(char.IsDigit(character) ? "AccentTextFillColorPrimaryBrush"
                            : char.IsLetter(character) ? "TextFillColorPrimaryBrush" : "SystemFillColorCautionBrush"),
                    });
            }
            cell.RevealIcon.Glyph = revealed ? "\uED1A" : "\uE890";
            cell.Reveal.IsEnabled = !_busy;
            AutomationProperties.SetName(cell.Reveal, $"{(revealed ? "隐藏" : "显示")}{cell.Label}");
            ToolTipService.SetToolTip(cell.Reveal, revealed ? "隐藏" : "显示（需 Windows Hello）");
        }
    }

    private static string Mask(string key, string value) => key switch
    {
        "number" => "•••• " + new string(value.Where(char.IsDigit).TakeLast(4).ToArray()),
        "cvv" => "•••",
        _ => "••••••••••••",
    };

    private static string? ValueOf(CipherDetail detail, string key) => (detail, key) switch
    {
        (LoginDetail value, "username") => value.Username,
        (LoginDetail value, "password") => value.Password,
        (LoginDetail value, "totp") => value.TotpSecret,
        (CardDetail value, "number") => value.Number,
        (CardDetail value, "expiry") => value.Expiry,
        (CardDetail value, "cvv") => value.Cvv,
        (IdentityDetail value, "fullName") => value.FullName,
        (IdentityDetail value, "phone") => value.Phone,
        (IdentityDetail value, "email") => value.Email,
        (SshDetail value, "publicKey") => value.PublicKey,
        (SshDetail value, "fingerprint") => value.Fingerprint,
        (SshDetail value, "privateKey") => value.PrivateKey,
        _ => null,
    };

    private async Task<bool> EnsureAvailableAsync(string key, bool reveal, string itemId, long version, CancellationToken ct)
    {
        if (Detail is not { IsDeleted: false } current || current.Id != itemId) return false;
        var cell = _cells.FirstOrDefault(candidate => candidate.Key == key);
        if (cell is null) return false;
        if (reveal || current.Reprompt || cell.Secret && !current.SensitiveUnlocked)
        {
            if (AuthorizeSensitiveActionAsync is null || !await AuthorizeSensitiveActionAsync(key, reveal, ct).WaitAsync(ct)) return false;
            if (!IsLoaded || _contextVersion != version || Detail?.Id != itemId) return false;
            var authorized = ReadAuthorizedDetail is null ? Detail : ReadAuthorizedDetail();
            if (authorized is null || authorized.Id != itemId || authorized.IsDeleted || cell.Secret && !authorized.SensitiveUnlocked) return false;
            if (!ReferenceEquals(authorized, Detail)) SetValue(DetailProperty, authorized);
        }
        return IsLoaded && _contextVersion == version && Detail?.Id == itemId && !string.IsNullOrEmpty(ValueOf(Detail, key));
    }

    private async Task CopyAsync(string key)
    {
        if (_busy || Detail is not { IsDeleted: false } current) return;
        var cell = _cells.FirstOrDefault(candidate => candidate.Key == key);
        if (cell is null) return;
        if (cell.Add) { AddTotpRequested?.Invoke(this, EventArgs.Empty); return; }
        var itemId = current.Id;
        var version = _contextVersion;
        using var cancellation = new CancellationTokenSource();
        var operationToken = cancellation.Token;
        _operationCancellation = cancellation;
        _busy = true;
        try
        {
            if (!await EnsureAvailableAsync(key, false, itemId, version, operationToken)) return;
            operationToken.ThrowIfCancellationRequested();
            var clipboard = global::App.App.Services?.GetService<IClipboardService>();
            var lifecycle = global::App.App.Services?.GetService<IAppLifecycleCoordinator>();
            if (clipboard is null || lifecycle?.TryRunSensitive(() =>
            {
                if (_contextVersion != version || Detail is not { IsDeleted: false } detail || detail.Id != itemId) return false;
                var value = ValueOf(detail, key);
                if (string.IsNullOrEmpty(value)) return false;
                if (key == "totp")
                {
                    if (!Crypto.TotpGenerator.TryGenerate(value, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var code)) return false;
                    clipboard.SetSecretText(code);
                }
                else clipboard.SetText(value);
                (clipboard as IClipboardStatus)?.SetLabel(cell.Label);
                return true;
            }) != true) return;
            _copiedField = key;
            _copiedVersion = _clipboardStatus?.WriteVersion ?? 0;
            var index = _cells.FindIndex(candidate => candidate.Key == key);
            _nextField = index >= 0 && index + 1 < _cells.Count && !_cells[index + 1].Add ? _cells[index + 1].Key : null;
            UpdateFeedback();
            Flash(_cells.First(candidate => candidate.Key == key));
            Copied?.Invoke(this, new CopyStripCopiedEventArgs(itemId, key, cell.Label));
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { }
        catch (Exception exception) when (Api.RequestTimeoutClassifier.IsTimeout(exception))
        {
            ShowMessage(UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or OperationCanceledException)
        {
            ShowMessage("复制失败，请重试。");
        }
        finally { _operationCancellation = null; _busy = false; RenderValues(); }
    }

    private async Task RevealAsync(string key)
    {
        if (_busy || Detail is not { IsDeleted: false } current) return;
        if (_revealed.Remove(key)) { RenderValues(); return; }
        var version = _contextVersion;
        var itemId = current.Id;
        using var cancellation = new CancellationTokenSource();
        var operationToken = cancellation.Token;
        _operationCancellation = cancellation;
        _busy = true;
        RenderValues();
        try
        {
            if (!await EnsureAvailableAsync(key, true, itemId, version, operationToken)) return;
            operationToken.ThrowIfCancellationRequested();
            var lifecycle = global::App.App.Services?.GetService<IAppLifecycleCoordinator>();
            lifecycle?.TryRunSensitive(() =>
            {
                if (_contextVersion != version || Detail?.Id != itemId) return false;
                _revealed.Add(key);
                return true;
            });
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { }
        catch (Exception exception) when (Api.RequestTimeoutClassifier.IsTimeout(exception))
        {
            ShowMessage(UiExceptionPolicy.RequestTimeoutMessage);
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or OperationCanceledException)
        {
            ShowMessage("无法完成验证，请重试。");
        }
        finally { _operationCancellation = null; _busy = false; RenderValues(); }
    }

    private void OnClipboardChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateFeedback);
    private void UpdateFeedback()
    {
        var active = _clipboardStatus is { HasContent: true } && _copiedVersion != 0 && _clipboardStatus.WriteVersion == _copiedVersion;
        var remaining = _clipboardStatus?.ClearAt is { } clearAt ? Math.Max(0, (int)Math.Ceiling((clearAt - DateTimeOffset.UtcNow).TotalSeconds)) : 0;
        foreach (var cell in _cells)
        {
            var copied = active && _copiedField == cell.Key;
            var next = !copied && _nextField == cell.Key && Detail?.IsDeleted == false;
            var color = BrushFor(copied ? "SystemFillColorSuccessBrush" : next ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            cell.LabelText.Text = copied ? "已复制" + (remaining > 0 ? $" · {remaining} 秒" : string.Empty) : next ? $"下一个 · {cell.Label}" : cell.Label;
            cell.LabelText.Foreground = cell.DoneIcon.Foreground = color;
            cell.LabelText.Margin = new Thickness(copied ? 6 : 0, 0, 0, 0);
            cell.LabelText.FontWeight = copied || next ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            cell.DoneIcon.Visibility = copied ? Visibility.Visible : Visibility.Collapsed;
            cell.CopyIcon.Opacity = cell.Hovered && !copied && !cell.Add && Detail?.IsDeleted == false ? 1 : 0;
            cell.CopyIcon.Foreground = BrushFor("TextFillColorSecondaryBrush");
            cell.Progress.Visibility = next || copied && _clipboardStatus?.TotalSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
            cell.Progress.Foreground = color;
            cell.Progress.Background = BrushFor("DividerStrokeColorDefaultBrush");
            cell.Progress.Value = next ? 100 : _clipboardStatus?.TotalSeconds > 0 ? 100d * remaining / _clipboardStatus.TotalSeconds : 0;
            cell.Flash.Background = BrushFor("AccentTextFillColorPrimaryBrush");
            if (cell.Totp is not null)
            {
                var inHeader = cell.Root.ActualWidth > 0 && cell.Root.ActualWidth < 194;
                cell.Totp.ShowCountdown = !inHeader;
                cell.CopyIcon.Visibility = inHeader ? Visibility.Collapsed : Visibility.Visible;
                var seed = Detail is { } detail ? ValueOf(detail, "totp") : null;
                var hasCode = IsLoaded && Crypto.TotpGenerator.TryGenerate(seed, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out _);
                cell.HeaderCountdown.Visibility = inHeader && hasCode ? Visibility.Visible : Visibility.Collapsed;
                if (inHeader && hasCode)
                {
                    var period = TotpField.GetPeriod(seed);
                    var seconds = period - DateTimeOffset.UtcNow.ToUnixTimeSeconds() % period;
                    cell.HeaderRing.Maximum = period;
                    cell.HeaderRing.Value = seconds;
                    cell.HeaderRing.Foreground = BrushFor(seconds <= 5 ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
                    cell.HeaderSeconds.Text = seconds.ToString();
                    cell.HeaderSeconds.Foreground = BrushFor("TextFillColorSecondaryBrush");
                    ToolTipService.SetToolTip(cell.HeaderCountdown, $"验证码每 {period} 秒刷新");
                    cell.CopyIcon.Opacity = 0;
                }
            }
        }
    }

    private Brush BrushFor(string key) => ThemeBrushResolver.Resolve(this, key);

    private static void Flash(Cell cell)
    {
        if (!new UISettings().AnimationsEnabled) return;
        var animation = new DoubleAnimation { From = .16, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(650)) };
        Storyboard.SetTarget(animation, cell.Flash);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void ClearVisualValues()
    {
        foreach (var cell in _cells)
        {
            cell.ValueText.Text = string.Empty;
            cell.ValueText.Inlines.Clear();
            if (cell.Totp is not null) cell.Totp.Secret = string.Empty;
        }
    }

    private void ShowMessage(string text)
    {
        FeedbackText.Text = text;
        FeedbackText.Visibility = Visibility.Visible;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void ClearMessage()
    {
        _feedbackTimer.Stop();
        if (FeedbackText is null) return;
        FeedbackText.Text = string.Empty;
        FeedbackText.Visibility = Visibility.Collapsed;
    }
}
