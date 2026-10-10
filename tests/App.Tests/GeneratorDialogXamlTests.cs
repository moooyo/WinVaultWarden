using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class GeneratorDialogXamlTests
{
    private static XDocument Load(string file = "GeneratorWorkspace.xaml")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", file);
            if (File.Exists(path))
                return XDocument.Load(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException(file);
    }

    private static XElement Control(string automationId) => Load().Descendants().Single(element =>
        element.Attribute("AutomationProperties.AutomationId")?.Value == automationId);

    [Fact]
    public void PageAndDialog_UseTheSameCredentialWorkspace()
    {
        Assert.Contains(Load("GeneratorPage.xaml").Descendants(), element => element.Name.LocalName == "GeneratorWorkspace");
        Assert.Contains(Load("GeneratorDialog.xaml").Descendants(), element => element.Name.LocalName == "GeneratorWorkspace");
    }

    [Fact]
    public void GeneratedValue_WrapsWithoutDisplacingCopyActions()
    {
        var value = Control("GeneratedCredentialValue");
        Assert.Equal("{StaticResource MonospaceFontFamily}", value.Attribute("FontFamily")?.Value);
        Assert.Equal("Wrap", value.Attribute("TextWrapping")?.Value);
        Assert.Contains("ViewModel.CanCopy", Control("GeneratorCopyButton").Attribute("IsEnabled")?.Value);
        Assert.Contains("ViewModel.CopyLabel", Control("GeneratorCopyButton").ToString());
    }

    [Theory]
    [InlineData("PasswordLengthSlider", "密码长度滑块")]
    [InlineData("PasswordLengthNumberBox", "密码长度数值")]
    public void PasswordLengthInputs_HaveDistinctAccessibleNames(string id, string name) =>
        Assert.Equal(name, Control(id).Attribute("AutomationProperties.Name")?.Value);

    [Fact]
    public void UsernameType_OffersOnlySupportedGenerationMethods()
    {
        var combo = Control("UsernameTypeComboBox");
        Assert.Equal(new[] { "随机单词", "加号邮箱", "全域邮箱" },
            combo.Elements().Select(element => element.Attribute("Content")?.Value));
        Assert.Contains("ViewModel.UsernameTypeIndex", combo.Attribute("SelectedIndex")?.Value);
        Assert.Contains("Mode=TwoWay", combo.Attribute("SelectedIndex")?.Value);
    }

    [Theory]
    [InlineData("UsernameCapitalizeToggle", "ViewModel.ShowUsernameWordOptions")]
    [InlineData("UsernameIncludeNumberToggle", "ViewModel.ShowUsernameWordOptions")]
    [InlineData("UsernameEmailTextBox", "ViewModel.ShowUsernameEmail")]
    [InlineData("UsernameDomainTextBox", "ViewModel.ShowUsernameDomain")]
    public void UsernameOptions_ExposeOnlyRelevantInputs(string id, string property)
    {
        var card = Control(id).Ancestors().First(element => element.Name.LocalName == "SettingsCard");
        Assert.Contains(property, card.Attribute("Visibility")?.Value);
        Assert.Contains("Mode=OneWay", card.Attribute("Visibility")?.Value);
    }

    [Theory]
    [InlineData("UsernameEmailTextBox", "ViewModel.UsernameEmail")]
    [InlineData("UsernameDomainTextBox", "ViewModel.UsernameDomain")]
    public void UsernameAddressInputs_UpdateValidationWhileTyping(string id, string property)
    {
        var input = Control(id);
        Assert.Contains(property, input.Attribute("Text")?.Value);
        Assert.Contains("Mode=TwoWay", input.Attribute("Text")?.Value);
        Assert.Contains("UpdateSourceTrigger=PropertyChanged", input.Attribute("Text")?.Value);
    }

    [Fact]
    public void OrganizationPolicyNotice_IsVisibleAndNonDismissible()
    {
        var notice = Control("GeneratorPolicyNotice");
        Assert.Contains("ViewModel.HasGeneratorPolicyNotice", notice.Attribute("IsOpen")?.Value);
        Assert.Contains("ViewModel.GeneratorPolicyNotice", notice.Attribute("Message")?.Value);
        Assert.Equal("False", notice.Attribute("IsClosable")?.Value);
    }

    [Fact]
    public void ModeSelection_EnforcesPolicyAndKeepsUsernameAvailable()
    {
        var modes = Control("GeneratorModeSelector");
        var choices = modes.Elements().ToArray();
        Assert.Equal(3, choices.Length);
        Assert.Contains("ViewModel.CanUsePasswordMode", choices[0].Attribute("IsEnabled")?.Value);
        Assert.Contains("ViewModel.CanUsePassphraseMode", choices[1].Attribute("IsEnabled")?.Value);
        Assert.Null(choices[2].Attribute("IsEnabled"));
        Assert.Contains("ViewModel.ModeIndex", modes.Attribute("SelectedIndex")?.Value);
    }

    [Theory]
    [InlineData("IncludeUppercaseCheckBox", "ViewModel.CanToggleUppercase")]
    [InlineData("IncludeLowercaseCheckBox", "ViewModel.CanToggleLowercase")]
    [InlineData("IncludeNumbersCheckBox", "ViewModel.CanToggleNumbers")]
    [InlineData("IncludeSpecialCheckBox", "ViewModel.CanToggleSpecial")]
    [InlineData("PassphraseCapitalizeToggle", "ViewModel.CanTogglePassphraseCapitalize")]
    [InlineData("PassphraseIncludeNumberToggle", "ViewModel.CanTogglePassphraseIncludeNumber")]
    public void RequiredPolicyOptions_CannotBeTurnedOff(string id, string property)
    {
        Assert.Contains(property, Control(id).Attribute("IsEnabled")?.Value);
        Assert.Contains("Mode=OneWay", Control(id).Attribute("IsEnabled")?.Value);
    }

    [Theory]
    [InlineData("PasswordLengthSlider", "ViewModel.MinimumPasswordLength")]
    [InlineData("PasswordLengthNumberBox", "ViewModel.MinimumPasswordLength")]
    [InlineData("MinNumbersNumberBox", "ViewModel.PolicyMinimumNumbers")]
    [InlineData("MinSpecialNumberBox", "ViewModel.PolicyMinimumSpecial")]
    [InlineData("PassphraseWordCountNumberBox", "ViewModel.MinimumPassphraseWordCount")]
    public void NumericInputs_ExposePolicyMinimums(string id, string property)
    {
        Assert.Contains(property, Control(id).Attribute("Minimum")?.Value);
        Assert.Contains("Mode=OneWay", Control(id).Attribute("Minimum")?.Value);
    }

    [Theory]
    [InlineData("MinNumbersNumberBox", "ViewModel.IncludeNumbers")]
    [InlineData("MinSpecialNumberBox", "ViewModel.IncludeSpecial")]
    public void MinimumInputs_AreEnabledOnlyForAvailableCharacterSets(string id, string property) =>
        Assert.Contains(property, Control(id).Attribute("IsEnabled")?.Value);

    [Fact]
    public void StrengthMeter_IsConditionalAndUsesTheRealScore()
    {
        var panel = Load().Descendants().Single(element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "StrengthPanel"));
        Assert.Contains("ViewModel.ShowStrength", panel.Attribute("Visibility")?.Value);
        Assert.Contains(panel.Descendants(), element =>
            element.Name.LocalName == "TextBlock" && element.Attribute("Text")?.Value.Contains("ViewModel.StrengthLabel") == true);
        Assert.Equal(4, panel.Descendants().Count(element => element.Name.LocalName == "Border"));
    }
}
