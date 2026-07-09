using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace App.Views;

public sealed partial class SecurityReportPage : Page
{
    public SecurityReportViewModel ViewModel { get; }

    public SecurityReportPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SecurityReportViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadOffline();
    }

    /// <summary>格式化重复密码分组标题,如 "3 个条目共用同一密码"。</summary>
    public static string FormatReusedHeader(int count) => $"{count} 个条目共用同一密码";

    /// <summary>"未发现已暴露的密码" 空态仅在检查已完成且确实无结果时显示,避免在尚未检查时误导用户。</summary>
    public static Visibility ShowNoExposedFound(bool exposedChecked, bool hasExposed) =>
        exposedChecked && !hasExposed ? Visibility.Visible : Visibility.Collapsed;
}
