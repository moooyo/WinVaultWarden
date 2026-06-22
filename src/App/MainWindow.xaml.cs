using App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        // 给系统标题栏按钮(最小化/最大化/关闭)留出右侧空间,避免与自定义内容重叠。
        SizeChanged += (_, _) => UpdateCaptionPadding();
        UpdateCaptionPadding();

        // 启动先进登录页;登录成功后由 LoginPage 切到主导航。
        ShowLogin();
    }

    private void UpdateCaptionPadding()
    {
        // RightInset 是物理像素,转成 DIP(除以光栅缩放)。
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        RightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
    }

    // 登录前:隐藏导航壳,只显示登录页(占满整窗,标题栏仅保留品牌)。
    public void ShowLogin()
    {
        Nav.Visibility = Visibility.Collapsed;
        TitleBarActions.Visibility = Visibility.Collapsed;
        LoginHost.Visibility = Visibility.Visible;
        LoginFrame.Navigate(typeof(LoginPage));
    }

    // 登录成功:显示主导航壳。
    public void ShowVault()
    {
        LoginHost.Visibility = Visibility.Collapsed;
        Nav.Visibility = Visibility.Visible;
        TitleBarActions.Visibility = Visibility.Visible;
        ContentFrame.Navigate(typeof(VaultPage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag as string;
        if (tag == "vault")
            ContentFrame.Navigate(typeof(VaultPage));
        else
            ContentFrame.Navigate(typeof(SimplePage), item.Content as string);
    }

    private void OnLogout(object sender, RoutedEventArgs e) => ShowLogin();
}
