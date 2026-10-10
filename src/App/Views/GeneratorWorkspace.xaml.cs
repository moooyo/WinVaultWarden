using System.ComponentModel;
using System.Security.Cryptography;
using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace App.Views;

public sealed partial class GeneratorWorkspace : UserControl
{
    private readonly DispatcherTimer _decodeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private int _decodeFrame;
    private bool _animateNextValue;
    private bool _loaded;
    public GeneratorViewModel ViewModel { get; }
    public event EventHandler? HistoryRequested;
    public event EventHandler? NewLoginRequested;
    public static string StateLabel(bool enabled) => enabled ? "开" : "关";

    public GeneratorWorkspace()
    {
        ViewModel = global::App.App.Services.GetRequiredService<GeneratorViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_loaded)
                RenderValue(_decodeTimer.IsEnabled ? _decodeFrame : 9);
        });
        _decodeTimer.Tick += (_, _) =>
        {
            _decodeFrame++;
            RenderValue(_decodeFrame);
            if (_decodeFrame >= 9)
                _decodeTimer.Stop();
        };
    }

    public void UseDialogLayout()
    {
        GeneratorModes.Visibility = Visibility.Visible;
        HistoryButton.Visibility = Visibility.Visible;
        NewLoginButton.Visibility = Visibility.Collapsed;
        GeneratedValueText.FontSize = 28;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.PrepareForDisplay();
        Bindings.Update();
        RenderValue();
        UpdateStrength();
        UpdateSeparators();
        UpdateFeedback();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _decodeTimer.Stop();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        GeneratedValueText.Inlines.Clear();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GeneratorViewModel.Mode))
            _animateNextValue = true;
        if (e.PropertyName == nameof(GeneratorViewModel.GeneratedPassword))
        {
            _decodeTimer.Stop();
            if (_animateNextValue && new UISettings().AnimationsEnabled && !string.IsNullOrEmpty(ViewModel.GeneratedPassword))
            {
                _decodeFrame = 0;
                RenderValue(0);
                _decodeTimer.Start();
            }
            else
                RenderValue();
            _animateNextValue = false;
        }
        if (e.PropertyName is nameof(GeneratorViewModel.CopyFailed) or nameof(GeneratorViewModel.HasCopyFeedback))
            UpdateFeedback();
        if (e.PropertyName is nameof(GeneratorViewModel.StrengthScore) or nameof(GeneratorViewModel.ShowStrength))
            UpdateStrength();
        if (e.PropertyName is nameof(GeneratorViewModel.PassphraseSeparator) or nameof(GeneratorViewModel.Mode))
            UpdateSeparators();
    }

    // Resolve generated runs from resources attached to this control's actual theme.
    private Brush Brush(string key) => key switch
    {
        "TextFillColorPrimaryBrush" => PrimaryBrushSource.Foreground,
        "TextFillColorTertiaryBrush" => TertiaryBrushSource.Foreground,
        "AccentTextFillColorPrimaryBrush" => AccentBrushSource.Foreground,
        "SystemFillColorCautionBrush" => CautionBrushSource.Foreground,
        "SystemFillColorCriticalBrush" => CriticalBrushSource.Foreground,
        "SystemFillColorSuccessBrush" => SuccessBrushSource.Foreground,
        _ => DividerBrushSource.Foreground,
    };

    private void RenderValue(int frame = 9)
    {
        var value = ViewModel.GeneratedPassword;
        GeneratedValueText.Inlines.Clear();
        var settled = value.Length * frame / 9;
        for (var index = 0; index < value.Length; index++)
        {
            var actual = value[index];
            var separator = ViewModel.IsPassphraseMode && !char.IsLetterOrDigit(actual);
            var character = index >= settled && !separator
                ? "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"[RandomNumberGenerator.GetInt32(36)] : actual;
            var key = char.IsDigit(character) ? "AccentTextFillColorPrimaryBrush"
                : char.IsLetter(character) ? "TextFillColorPrimaryBrush"
                : separator ? "TextFillColorTertiaryBrush" : "SystemFillColorCautionBrush";
            GeneratedValueText.Inlines.Add(new Run { Text = character.ToString(), Foreground = Brush(key) });
        }
        AutomationProperties.SetName(GeneratedValueText, ViewModel.GeneratedValueLabel);
        UpdateStrength();
    }

    private void UpdateStrength()
    {
        var active = Brush(ViewModel.StrengthSegments switch
        {
            1 => "SystemFillColorCriticalBrush",
            2 => "SystemFillColorCautionBrush",
            _ => "SystemFillColorSuccessBrush",
        });
        var inactive = Brush("DividerStrokeColorDefaultBrush");
        var segments = new[] { StrengthSegment1, StrengthSegment2, StrengthSegment3, StrengthSegment4 };
        for (var index = 0; index < segments.Length; index++)
            segments[index].Background = index < ViewModel.StrengthSegments ? active : inactive;
        StrengthText.Foreground = active;
        if (GeneratorModes.Visibility != Visibility.Visible)
            NewLoginButton.Visibility = ViewModel.IsUsernameMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateFeedback()
    {
        CopyFeedbackBar.Severity = InfoBarSeverity.Error;
        CopyIcon.Glyph = ViewModel.HasCopyFeedback && !ViewModel.CopyFailed ? "\uE73E" : "\uE8C8";
    }

    private void UpdateSeparators()
    {
        foreach (var button in new[] { SeparatorDash, SeparatorDot, SeparatorUnderscore, SeparatorSpace })
            button.IsChecked = (string)button.Tag == ViewModel.PassphraseSeparator;
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 600;
        StrengthActions.RowSpacing = narrow ? 12 : 0;
        Grid.SetRow(ActionButtons, narrow ? 1 : 0);
        Grid.SetColumn(ActionButtons, narrow ? 0 : 1);
        Grid.SetColumnSpan(ActionButtons, narrow ? 2 : 1);
        Grid.SetColumnSpan(StrengthPanel, narrow ? 2 : 1);
        LengthSlider.Width = WordsSlider.Width = Math.Clamp(e.NewSize.Width - 180, 140, 280);
    }

    private void OnRegenerateClick(object sender, RoutedEventArgs e)
    {
        _animateNextValue = true;
        ViewModel.RegenerateCommand.Execute(null);
    }

    private void OnSeparatorClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string separator })
            ViewModel.PassphraseSeparator = separator;
        UpdateSeparators();
    }

    private static bool IsToggleTap(TappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null)
        {
            if (element is ToggleSwitch)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnAvoidAmbiguousRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsToggleTap(e))
            ViewModel.AvoidAmbiguous = !ViewModel.AvoidAmbiguous;
    }

    private void OnCapitalizeRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsToggleTap(e) && ViewModel.CanTogglePassphraseCapitalize)
            ViewModel.PassphraseCapitalize = !ViewModel.PassphraseCapitalize;
    }

    private void OnIncludeNumberRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsToggleTap(e) && ViewModel.CanTogglePassphraseIncludeNumber)
            ViewModel.PassphraseIncludeNumber = !ViewModel.PassphraseIncludeNumber;
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke(this, EventArgs.Empty);
    private void OnNewLoginClick(object sender, RoutedEventArgs e) => NewLoginRequested?.Invoke(this, EventArgs.Empty);
}
