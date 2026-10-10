using App.ViewModels;
using App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;

namespace App.Views;

public sealed partial class SecurityReportPage : Page
{
    private readonly HashSet<FontIcon> _severityIcons = new();
    public SecurityReportViewModel ViewModel { get; }

    public SecurityReportPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SecurityReportViewModel>();
        InitializeComponent();
        ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var icon in _severityIcons)
                UpdateSeverityIcon(icon);
        });
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.RefreshReportsAsync();
    }

    public void SelectCategory(string category) => ViewModel.Category = category;

    public Task RefreshAsync() => ViewModel.RefreshReportsAsync();

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PageLayout.Padding = e.NewSize.Width < 540 ? new Thickness(24,32,24,40) : new Thickness(40,40,40,48);
    }

    private void OnIssueRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid && grid.FindName("IssueActions") is StackPanel actions)
        {
            var narrow = e.NewSize.Width < 620;
            grid.RowSpacing = narrow ? 8 : 0;
            Grid.SetRow(actions, narrow ? 1 : 0);
            Grid.SetColumn(actions, narrow ? 1 : 2);
            Grid.SetColumnSpan(actions, narrow ? 2 : 1);
        }
    }

    private void OnFixItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SecurityIssueRow row })
        {
            var route = row.Category == "http" ? "vault:edit:" : "vault:change-password:";
            global::App.App.Services.GetRequiredService<INavigationService>().GoToVault(route + row.CipherId);
        }
    }

    public static string CategoryIcon(string category) => category switch
    {
        "breach" => "\uEA39",
        "reuse" => "\uE895",
        "http" => "\uE774",
        _ => "\uE7BA",
    };

    public static string EmptyIcon(bool exposureChecked) => exposureChecked ? "\uE73E" : "\uE946";

    private void OnSeverityIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FontIcon icon)
        {
            _severityIcons.Add(icon);
            UpdateSeverityIcon(icon);
        }
    }

    private void OnSeverityIconUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FontIcon icon)
            _severityIcons.Remove(icon);
    }

    private void UpdateSeverityIcon(FontIcon icon) => icon.Foreground = (icon.Tag as string) switch
    {
        "critical" or "breach" => CriticalBrushSource.Foreground,
        "neutral" or "http" => SecondaryBrushSource.Foreground,
        _ => CautionBrushSource.Foreground,
    };

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.CancelOfflineAnalysis();
        ViewModel.CancelExposedCheck();
        base.OnNavigatedFrom(e);
    }

    private void OnOpenItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string cipherId } && !string.IsNullOrWhiteSpace(cipherId))
            global::App.App.Services.GetRequiredService<INavigationService>().GoToVault("vault:item:" + cipherId);
    }

    public static string FormatOpenItem(string name) => $"查看条目：{name}";

    /// <summary>Formats a group without exposing the reused password.</summary>
    public static string FormatReusedHeader(int count) => $"{count} 个条目共用同一密码";

    /// <summary>Shows a clean result only after a successful check.</summary>
    public static Visibility ShowNoExposedFound(bool exposedChecked, bool hasExposed) =>
        exposedChecked && !hasExposed ? Visibility.Visible : Visibility.Collapsed;
}
