using App.Services;
using App.ViewModels;
using System.Xml.Linq;
using Xunit;

namespace App.Tests;

// ============================================================================
// TDD: 浏览器集成仅便携版 — SettingsViewModel 门控、XAML 锁定、App 启动守卫
// ============================================================================

/// <summary>
/// SettingsViewModel 在 browserIntegrationSupported=false 时的门控行为。
/// </summary>
public class SettingsViewModelBrowserGateTests
{
    [Fact]
    public void BrowserIntegrationSupported_TrueByDefault()
    {
        var vm = new SettingsViewModel();
        Assert.True(vm.BrowserIntegrationSupported);
    }

    [Fact]
    public void BrowserIntegrationSupported_FalseWhenConstructedWithFalse()
    {
        var vm = new SettingsViewModel(browserIntegrationSupported: false);
        Assert.False(vm.BrowserIntegrationSupported);
    }

    [Fact]
    public void AllowBrowserIntegration_IsFalse_WhenNotSupported_EvenIfPrefWasTrue()
    {
        AppPreferences.Current.AllowBrowserIntegration = true;
        try
        {
            var vm = new SettingsViewModel(browserIntegrationSupported: false);
            Assert.False(vm.AllowBrowserIntegration);
        }
        finally
        {
            AppPreferences.Current.AllowBrowserIntegration = false;
            AppPreferences.Save();
        }
    }

    [Fact]
    public void AllowBrowserIntegration_CorrectsPref_WhenNotSupportedAndPrefWasTrue()
    {
        AppPreferences.Current.AllowBrowserIntegration = true;
        try
        {
            _ = new SettingsViewModel(browserIntegrationSupported: false);
            Assert.False(AppPreferences.Current.AllowBrowserIntegration);
        }
        finally
        {
            AppPreferences.Current.AllowBrowserIntegration = false;
            AppPreferences.Save();
        }
    }

    [Fact]
    public void AllowBrowserIntegration_CannotBeSetTrue_WhenNotSupported()
    {
        AppPreferences.Current.AllowBrowserIntegration = false;
        var vm = new SettingsViewModel(browserIntegrationSupported: false);
        vm.AllowBrowserIntegration = true;
        Assert.False(vm.AllowBrowserIntegration);
    }

    [Fact]
    public void AllowBrowserIntegration_DoesNotPersistTrue_WhenNotSupported()
    {
        AppPreferences.Current.AllowBrowserIntegration = false;
        var vm = new SettingsViewModel(browserIntegrationSupported: false);
        vm.AllowBrowserIntegration = true;
        Assert.False(AppPreferences.Current.AllowBrowserIntegration);
    }

    [Fact]
    public void AllowBrowserIntegration_DoesNotFireEvent_WhenNotSupported()
    {
        AppPreferences.Current.AllowBrowserIntegration = false;
        bool? received = null;
        void Handler(bool v) => received = v;
        SettingsViewModel.AllowBrowserIntegrationSettingChanged += Handler;
        try
        {
            var vm = new SettingsViewModel(browserIntegrationSupported: false);
            vm.AllowBrowserIntegration = true;
            Assert.Null(received); // 事件绝不能在不支持时触发
        }
        finally
        {
            SettingsViewModel.AllowBrowserIntegrationSettingChanged -= Handler;
        }
    }

    [Fact]
    public void AllowBrowserIntegration_WorksNormally_WhenSupported()
    {
        AppPreferences.Current.AllowBrowserIntegration = false;
        bool? received = null;
        void Handler(bool v) => received = v;
        SettingsViewModel.AllowBrowserIntegrationSettingChanged += Handler;
        try
        {
            var vm = new SettingsViewModel(browserIntegrationSupported: true);
            vm.AllowBrowserIntegration = true;
            Assert.True(vm.AllowBrowserIntegration);
            Assert.True(AppPreferences.Current.AllowBrowserIntegration);
            Assert.Equal(true, received);
        }
        finally
        {
            SettingsViewModel.AllowBrowserIntegrationSettingChanged -= Handler;
            AppPreferences.Current.AllowBrowserIntegration = false;
            AppPreferences.Save();
        }
    }
}

/// <summary>
/// XAML 结构断言：浏览器集成开关在不支持时通过绑定 disabled，描述改为"仅便携版支持"。
/// </summary>
public class SettingsPageBrowserIntegrationXamlTests
{
    private static XDocument LoadXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "SettingsPage.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("SettingsPage.xaml not found.");
    }

    [Fact]
    public void BrowserIntegrationCard_HasPortableOnlyDescription()
    {
        var doc = LoadXaml();
        Assert.Contains("仅便携版支持", doc.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserIntegrationToggle_IsEnabled_BoundToBrowserIntegrationSupported()
    {
        var doc = LoadXaml();
        var toggle = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ToggleSwitch"
                && e.Attributes().Any(a => a.Name.LocalName == "IsOn"
                    && a.Value.Contains("AllowBrowserIntegration")));
        Assert.NotNull(toggle);
        var isEnabled = toggle!.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "IsEnabled");
        Assert.NotNull(isEnabled);
        Assert.Contains("BrowserIntegrationSupported", isEnabled!.Value, StringComparison.Ordinal);
    }
}

/// <summary>
/// 源码结构守卫：App 启动和 DI 都正确处理 packaged 场景，不在 MSIX 下启动 bridge。
/// </summary>
public class BrowserIntegrationAppWiringSourceTests
{
    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    [Fact]
    public void AboutInfo_HasIsPackaged_Property()
    {
        var src = ReadSource("src", "App", "Services", "AboutInfo.cs");
        Assert.Contains("IsPackaged", src, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLaunch_Guards_BridgeStart_WithIsPackaged_Check()
    {
        var src = ReadSource("src", "App", "App.xaml.cs");
        // App 启动时必须检查 IsPackaged 才能启动 bridge
        Assert.Contains("IsPackaged", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceConfig_PassesBrowserIntegrationSupported_ToSettingsViewModel()
    {
        var src = ReadSource("src", "App", "Services", "ServiceConfiguration.cs");
        Assert.Contains("browserIntegrationSupported", src, StringComparison.Ordinal);
        Assert.Contains("IsPackaged", src, StringComparison.Ordinal);
    }

    [Fact]
    public void AppCsproj_BrowserHostComment_NoLongerMentionsMsixRoot()
    {
        var src = ReadSource("src", "App", "App.csproj");
        Assert.DoesNotContain("MSIX 根目录", src, StringComparison.Ordinal);
    }
}
