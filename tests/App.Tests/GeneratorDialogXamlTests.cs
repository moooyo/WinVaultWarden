using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class GeneratorDialogXamlTests
{
    internal static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "GeneratorDialog.xaml");
            if (File.Exists(path)) return XDocument.Load(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("GeneratorDialog.xaml not found.");
    }

    private static string LoadCodeBehind()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "GeneratorDialog.xaml.cs");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("GeneratorDialog.xaml.cs not found.");
    }

    [Fact]
    public void GeneratedValueTextBlocks_UseMonospaceResource_NoInlineConsolas()
    {
        var doc = Load();
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Attribute("FontFamily")?.Value == "Consolas");

        var monoCount = doc.Descendants()
            .Count(e => e.Attribute("FontFamily")?.Value == "{StaticResource MonospaceFontFamily}");
        Assert.Equal(3, monoCount);
    }

    [Fact]
    public void PrimaryButton_DescribesItsCopyOnlyAction()
    {
        var dialog = Load().Root;

        Assert.NotNull(dialog);
        Assert.Equal("复制", (string?)dialog!.Attribute("PrimaryButtonText"));
    }

    [Fact]
    public void ResultCardBorders_UseCardBorderStyle_AndCompactPadding()
    {
        var doc = Load();
        // 每个 Tab 的结果卡片仍是手写 Border(选项行已迁移到 SettingsCard),共 3 个。
        var styled = doc.Descendants()
            .Where(e => e.Name.LocalName == "Border"
                && e.Attribute("Style")?.Value == "{StaticResource CardBorderStyle}")
            .ToList();
        Assert.Equal(3, styled.Count);

        Assert.All(styled, e =>
            Assert.Equal("{StaticResource CardContentPaddingCompact}", e.Attribute("Padding")?.Value));

        // 选项行不再用整卡 Border 包裹,CardContentPadding(非 Compact)不应再出现。
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Attribute("Padding")?.Value == "{StaticResource CardContentPadding}");
    }

    [Fact]
    public void UsesTkNamespace_ForSettingsCard()
    {
        var doc = Load();
        var tkNs = doc.Root?.Attributes()
            .FirstOrDefault(a => a.Name.LocalName == "tk" && a.Value == "using:CommunityToolkit.WinUI.Controls");
        Assert.NotNull(tkNs);
    }

    [Fact]
    public void OptionsUseMultipleSettingsCards()
    {
        var doc = Load();
        var cards = doc.Descendants().Where(e => e.Name.LocalName == "SettingsCard").ToList();
        // Password (5) + passphrase (4) + username (5) + history entry (1) = 15.
        Assert.Equal(15, cards.Count);
    }

    [Fact]
    public void StrengthMeter_ProgressBarBoundToStrengthFraction()
    {
        var doc = Load();
        var bars = doc.Descendants()
            .Where(e => e.Name.LocalName == "ProgressBar"
                && (e.Attribute("Value")?.Value.Contains("ViewModel.StrengthFraction") ?? false))
            .ToList();
        // 密码 + 密码短语 两个 Tab 各一个,用户名 Tab 不含强度条。
        Assert.Equal(2, bars.Count);
        Assert.All(bars, e => Assert.Contains("Mode=OneWay", e.Attribute("Value")!.Value));
    }

    [Theory]
    [InlineData("PasswordLengthSlider", "密码长度滑块")]
    [InlineData("PasswordLengthNumberBox", "密码长度数值")]
    public void PasswordLengthInputs_HaveDistinctAccessibleNames(string automationId, string accessibleName)
    {
        var input = Load().Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == automationId);

        Assert.Equal(accessibleName, input.Attribute("AutomationProperties.Name")?.Value);
    }

    [Fact]
    public void PasswordOptions_UseResponsiveGridsInsteadOfSingleHorizontalRows()
    {
        var doc = Load();
        var lengthCard = doc.Descendants().Single(e =>
            e.Name.LocalName == "SettingsCard" && (string?)e.Attribute("Header") == "长度");
        var characterCard = doc.Descendants().Single(e =>
            e.Name.LocalName == "SettingsCard" && (string?)e.Attribute("Header") == "包含字符");

        Assert.Equal("Stretch", (string?)lengthCard.Attribute("HorizontalContentAlignment"));
        Assert.Equal("Grid", lengthCard.Elements().Single().Name.LocalName);
        Assert.Equal("Stretch", (string?)characterCard.Attribute("HorizontalContentAlignment"));
        Assert.Equal("Grid", characterCard.Elements().Single().Name.LocalName);
        Assert.DoesNotContain(characterCard.Descendants(), e =>
            e.Name.LocalName == "StackPanel" && (string?)e.Attribute("Orientation") == "Horizontal");
    }

    [Fact]
    public void UsernameType_UsesExplicitComboBoxWithOnlySupportedOptions()
    {
        var doc = Load();
        var comboBox = doc.Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == "UsernameTypeComboBox");
        var options = comboBox.Elements()
            .Where(e => e.Name.LocalName == "ComboBoxItem")
            .Select(e => (string?)e.Attribute("Content"))
            .ToArray();

        Assert.Equal(new[] { "随机单词", "加号邮箱", "全域邮箱" }, options);
        Assert.Contains("ViewModel.UsernameTypeIndex", comboBox.Attribute("SelectedIndex")?.Value);
        Assert.Contains("Mode=TwoWay", comboBox.Attribute("SelectedIndex")?.Value);
        Assert.DoesNotContain(doc.ToString(), "转发的电子邮箱别名", StringComparison.Ordinal);
        Assert.DoesNotContain(doc.ToString(), "ForwardedEmailAlias", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UsernameCapitalizeToggle", "ViewModel.ShowUsernameWordOptions")]
    [InlineData("UsernameIncludeNumberToggle", "ViewModel.ShowUsernameWordOptions")]
    [InlineData("UsernameEmailTextBox", "ViewModel.ShowUsernameEmail")]
    [InlineData("UsernameDomainTextBox", "ViewModel.ShowUsernameDomain")]
    public void UsernameOptionControls_UseModeSpecificVisibility(
        string automationId,
        string visibilityProperty)
    {
        var control = Load().Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == automationId);
        var card = control.Ancestors().First(e => e.Name.LocalName == "SettingsCard");

        Assert.Contains(visibilityProperty, card.Attribute("Visibility")?.Value);
        Assert.Contains("Converter={StaticResource BoolToVis}", card.Attribute("Visibility")?.Value);
    }

    [Theory]
    [InlineData("UsernameEmailTextBox", "ViewModel.UsernameEmail")]
    [InlineData("UsernameDomainTextBox", "ViewModel.UsernameDomain")]
    public void UsernameAddressInputs_RegenerateFromTwoWayPropertyChangedBindings(
        string automationId,
        string property)
    {
        var input = Load().Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == automationId);
        var binding = input.Attribute("Text")?.Value;

        Assert.Contains(property, binding);
        Assert.Contains("Mode=TwoWay", binding);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", binding);
        Assert.Equal("Stretch", (string?)input.Attribute("HorizontalAlignment"));
        Assert.Equal("0", (string?)input.Attribute("MinWidth"));
    }

    [Theory]
    [InlineData("UsernameRegenerateButton")]
    [InlineData("UsernameCopyButton")]
    public void UsernameResultActions_AreDisabledWhenGenerationIsInvalid(string automationId)
    {
        var button = Load().Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == automationId);

        Assert.Contains("ViewModel.CanGenerate", button.Attribute("IsEnabled")?.Value);
        Assert.Contains("Mode=OneWay", button.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void UsernameWordNumberLabel_StatesFourDigitBehavior()
    {
        var card = Load().Descendants().Single(e =>
            e.Name.LocalName == "SettingsCard"
            && (string?)e.Attribute("Header") == "包含 4 位数字");

        Assert.Contains(card.Descendants(), e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == "UsernameIncludeNumberToggle");
    }

    [Fact]
    public void OrganizationPolicyNotice_IsBoundToPolicyState()
    {
        var notice = Load().Descendants().Single(e =>
            e.Name.LocalName == "InfoBar"
            && e.Attribute("AutomationProperties.AutomationId")?.Value == "GeneratorPolicyNotice");

        Assert.Contains(
            "ViewModel.HasGeneratorPolicyNotice",
            notice.Attribute("IsOpen")?.Value);
        Assert.Contains("Mode=OneWay", notice.Attribute("IsOpen")?.Value);
        Assert.Contains(
            "ViewModel.GeneratorPolicyNotice",
            notice.Attribute("Message")?.Value);
        Assert.Equal("False", notice.Attribute("IsClosable")?.Value);
    }

    [Theory]
    [InlineData("PasswordTab", "ViewModel.CanUsePasswordMode")]
    [InlineData("PassphraseTab", "ViewModel.CanUsePassphraseMode")]
    public void PasswordTypeTabs_AreDisabledByOrganizationPolicy(
        string tabName,
        string policyProperty)
    {
        var xamlName = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        var tab = Load().Descendants().Single(e =>
            e.Name.LocalName == "TabViewItem"
            && e.Attribute(xamlName)?.Value == tabName);

        Assert.Contains(policyProperty, tab.Attribute("IsEnabled")?.Value);
        Assert.Contains("Mode=OneWay", tab.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void UsernameTab_RemainsAvailableWhenPasswordTypeIsOverridden()
    {
        var xamlName = XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        var tab = Load().Descendants().Single(e =>
            e.Name.LocalName == "TabViewItem"
            && e.Attribute(xamlName)?.Value == "UsernameTab");

        Assert.Null(tab.Attribute("IsEnabled"));
    }

    [Theory]
    [InlineData("IncludeUppercaseCheckBox", "ViewModel.CanToggleUppercase")]
    [InlineData("IncludeLowercaseCheckBox", "ViewModel.CanToggleLowercase")]
    [InlineData("IncludeNumbersCheckBox", "ViewModel.CanToggleNumbers")]
    [InlineData("IncludeSpecialCheckBox", "ViewModel.CanToggleSpecial")]
    [InlineData("PassphraseCapitalizeToggle", "ViewModel.CanTogglePassphraseCapitalize")]
    [InlineData("PassphraseIncludeNumberToggle", "ViewModel.CanTogglePassphraseIncludeNumber")]
    public void RequiredPolicyOptions_CannotBeTurnedOff(
        string automationId,
        string policyProperty)
    {
        var control = Load().Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == automationId);

        Assert.Contains(policyProperty, control.Attribute("IsEnabled")?.Value);
        Assert.Contains("Mode=OneWay", control.Attribute("IsEnabled")?.Value);
    }

    [Theory]
    [InlineData("PasswordLengthSlider", "ViewModel.MinimumPasswordLength")]
    [InlineData("PasswordLengthNumberBox", "ViewModel.MinimumPasswordLength")]
    [InlineData("MinNumbersNumberBox", "ViewModel.PolicyMinimumNumbers")]
    [InlineData("MinSpecialNumberBox", "ViewModel.PolicyMinimumSpecial")]
    [InlineData("PassphraseWordCountNumberBox", "ViewModel.MinimumPassphraseWordCount")]
    public void NumericInputs_ExposePolicyMinimums(
        string automationId,
        string policyProperty)
    {
        var control = Load().Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == automationId);

        Assert.Contains(policyProperty, control.Attribute("Minimum")?.Value);
        Assert.Contains("Mode=OneWay", control.Attribute("Minimum")?.Value);
    }

    [Theory]
    [InlineData("MinNumbersNumberBox", "ViewModel.IncludeNumbers")]
    [InlineData("MinSpecialNumberBox", "ViewModel.IncludeSpecial")]
    public void MinimumInputs_AreEnabledOnlyForAvailableCharacterSets(string automationId, string property)
    {
        var input = Load().Descendants().Single(e =>
            e.Attribute("AutomationProperties.AutomationId")?.Value == automationId);

        Assert.Contains(property, input.Attribute("IsEnabled")?.Value);
        Assert.Contains("Mode=OneWay", input.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void StrengthMeter_LabelBoundToStrengthLabel()
    {
        var doc = Load();
        var labels = doc.Descendants()
            .Where(e => e.Name.LocalName == "TextBlock"
                && (e.Attribute("Text")?.Value.Contains("ViewModel.StrengthLabel") ?? false))
            .ToList();
        Assert.Equal(2, labels.Count);
    }

    [Fact]
    public void StrengthPanel_VisibilityBoundToShowStrength()
    {
        var doc = Load();
        var panels = doc.Descendants()
            .Where(e => e.Attribute("Visibility")?.Value.Contains("ViewModel.ShowStrength") ?? false)
            .ToList();
        Assert.Equal(2, panels.Count);
        Assert.All(panels, e => Assert.Contains("Converter={StaticResource BoolToVis}", e.Attribute("Visibility")!.Value));
    }

    [Fact]
    public void UsernameTab_HasNoStrengthMeter()
    {
        var doc = Load();
        var usernameTab = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TabViewItem"
                && (string?)e.Attribute("Header") == "用户名");
        Assert.NotNull(usernameTab);
        Assert.DoesNotContain(usernameTab!.Descendants(), e => e.Name.LocalName == "ProgressBar");
    }

    [Fact]
    public void AvoidAmbiguous_IsToggleSwitch_BoundTwoWay()
    {
        var doc = Load();
        var toggle = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ToggleSwitch"
                && (e.Attribute("IsOn")?.Value.Contains("ViewModel.AvoidAmbiguous") ?? false));
        Assert.NotNull(toggle);
        Assert.Contains("Mode=TwoWay", toggle!.Attribute("IsOn")!.Value);

        // 旧的 CheckBox 版本不应再存在。
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Name.LocalName == "CheckBox"
                && (e.Attribute("IsChecked")?.Value.Contains("ViewModel.AvoidAmbiguous") ?? false));
    }

    [Fact]
    public void HistoryEntry_IsClickableSettingsCard_WithHandler()
    {
        var doc = Load();
        var card = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "SettingsCard"
                && (string?)e.Attribute("Header") == "生成器历史记录");
        Assert.NotNull(card);
        Assert.Equal("True", (string?)card!.Attribute("IsClickEnabled"));
        Assert.Equal("OnHistoryClick", (string?)card.Attribute("Click"));

        // 旧的手写 Button/Grid 历史记录行不应再存在。
        Assert.DoesNotContain(doc.Descendants(),
            e => e.Name.LocalName == "Button" && (string?)e.Attribute("Click") == "OnHistoryClick");
    }

    [Fact]
    public void CodeBehind_PreservesEventHandlers()
    {
        var src = LoadCodeBehind();
        Assert.Contains("OnPrimaryButtonClick", src, StringComparison.Ordinal);
        Assert.Contains("OnGeneratorTabSelectionChanged", src, StringComparison.Ordinal);
        Assert.Contains("OnHistoryClick", src, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeBehind_SynchronizesTabsAfterPolicyForcesMode()
    {
        var src = LoadCodeBehind();

        Assert.Contains("ViewModel.PropertyChanged += OnViewModelPropertyChanged", src, StringComparison.Ordinal);
        Assert.Contains("SelectModeTab", src, StringComparison.Ordinal);
        Assert.Contains("GeneratorMode.Passphrase => 1", src, StringComparison.Ordinal);
        Assert.Contains("GeneratorMode.Username => 2", src, StringComparison.Ordinal);
    }

    [Fact]
    public void TabView_RetainsNamesAndSelectionHandler()
    {
        var doc = Load();
        var tabView = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TabView");
        Assert.NotNull(tabView);
        Assert.Equal("GeneratorTabs", (string?)tabView!.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")));
        Assert.Equal("OnGeneratorTabSelectionChanged", (string?)tabView.Attribute("SelectionChanged"));

        var passwordTab = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TabViewItem"
                && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "PasswordTab");
        Assert.NotNull(passwordTab);
    }
}
