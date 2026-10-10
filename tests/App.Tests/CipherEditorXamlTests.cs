using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class CipherEditorXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void VaultPage_AddMenu_GatesNegotiatedCipherTypesByCapability()
    {
        var document = LoadVaultPageXaml();
        var addButton = RequireByName(document, "AddCipherButton");
        var flyoutItems = addButton.Descendants(Xaml + "MenuFlyoutItem")
            .Select(element => element.Attribute("Text")?.Value)
            .Where(text => text is not null)
            .ToArray();

        Assert.Contains("登录", flyoutItems);
        Assert.Contains("支付卡", flyoutItems);
        Assert.Contains("身份", flyoutItems);
        Assert.Contains("笔记", flyoutItems);
        Assert.Contains("SSH 密钥", flyoutItems);
        Assert.Contains("银行账户", flyoutItems);
        Assert.Contains("驾驶证", flyoutItems);
        Assert.Contains("护照", flyoutItems);
        Assert.Contains("文件夹", flyoutItems);
        Assert.All(
            addButton.Descendants(Xaml + "MenuFlyoutItem")
                .Where(item => item.Attribute("Text")?.Value is "银行账户" or "驾驶证" or "护照"),
            item => Assert.Contains(
                "CanCreateNewCipherTypes",
                item.Attribute("Visibility")?.Value));
    }

    [Fact]
    public void VaultPage_EditorTypeSelector_GatesNegotiatedTypesByCapability()
    {
        var document = LoadVaultPageXaml();
        var typeBox = RequireByName(document, "CipherEditorTypeBox");
        var tags = typeBox.Elements(Xaml + "ComboBoxItem")
            .Select(item => item.Attribute("Tag")?.Value)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "Login", "Card", "Identity", "Note", "Ssh",
                "BankAccount", "DriversLicense", "Passport",
            },
            tags);
        Assert.All(
            typeBox.Elements(Xaml + "ComboBoxItem").Skip(5),
            item => Assert.Contains(
                "CanCreateNewCipherTypes",
                item.Attribute("Visibility")?.Value));
        Assert.Contains("CanChangeEditorType", typeBox.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void VaultPage_EditorActions_HaveStableNames()
    {
        var document = LoadVaultPageXaml();

        RequireByName(document, "SaveCipherEditorButton");
        RequireByName(document, "CancelCipherEditorButton");
        RequireByName(document, "CipherEditorPanel");
        RequireByName(document, "CipherEditorTypeBox");
        RequireByName(document, "CipherEditorDestinationBox");
        RequireByName(document, "CipherEditorFolderBox");
        RequireByName(document, "AddCustomFieldButton");
        RequireByName(document, "RemoveCustomFieldButton");
        RequireByName(document, "CustomFieldsEditorItems");
        RequireByName(document, "AddFolderMenuItem");
    }

    [Fact]
    public void VaultPage_PendingCreateRecoveryIsGlobalAndKeepsBothActionsReachable()
    {
        var document = LoadVaultPageXaml();
        var editor = RequireByName(document, "CipherEditorPanel");
        var statusPanel = RequireByName(document, "VaultGlobalStatusPanel");
        var recovery = RequireByName(document, "PendingCipherCreateRecoveryPanel");
        var reconcile = RequireByName(document, "PendingCipherCreateCheckButton");
        var abandon = RequireByName(document, "PendingCipherCreateAbandonButton");
        var back = RequireByName(document, "VaultNarrowBackButton");

        Assert.Contains("CanEditEditorDraft", editor.Attribute("IsEnabled")?.Value);
        Assert.Equal("0", statusPanel.Attribute("Grid.Row")?.Value);
        Assert.Equal("2", statusPanel.Attribute("Grid.ColumnSpan")?.Value);
        Assert.Contains(statusPanel, recovery.Ancestors());
        Assert.DoesNotContain(
            recovery.Ancestors(),
            ancestor => ancestor.Attribute(X + "Name")?.Value is "VaultListPane" or "VaultDetailPane");
        Assert.Contains("HasPendingCipherCreate", recovery.Attribute("Visibility")?.Value);
        Assert.Contains("SyncCommand", reconcile.Attribute("Command")?.Value);
        Assert.Contains("CanCheckPendingCipherCreate", reconcile.Attribute("IsEnabled")?.Value);
        Assert.Contains("CanAbandonPendingCipherCreate", abandon.Attribute("IsEnabled")?.Value);
        Assert.Equal("OnAbandonPendingCipherCreateClick", abandon.Attribute("Click")?.Value);
        Assert.Contains("CanCancelEditor", back.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void VaultPage_FolderMenuItem_IsEnabledAndWired()
    {
        var document = LoadVaultPageXaml();
        var folderItem = RequireByName(document, "AddFolderMenuItem");

        Assert.Equal("文件夹", folderItem.Attribute("Text")?.Value);
        Assert.Null(folderItem.Attribute("IsEnabled")); // no longer disabled
        Assert.Equal("OnAddFolderClick", folderItem.Attribute("Click")?.Value);
    }

    [Fact]
    public void VaultPage_RowMenu_ExposesGuardedCloneAction()
    {
        var document = LoadVaultPageXaml();
        var clone = document.Descendants(Xaml + "MenuFlyoutItem")
            .Single(item => item.Attribute("Click")?.Value == "OnRowCloneClick");

        Assert.Equal("创建副本", clone.Attribute("Text")?.Value);
        Assert.Contains("CanClone", clone.Attribute("IsEnabled")?.Value);
        Assert.Contains("IsDeleted", clone.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void VaultPage_SensitiveActionsBindToPermissionCapabilities()
    {
        var document = LoadVaultPageXaml();
        var rowEdit = document.Descendants(Xaml + "MenuFlyoutItem")
            .Single(item => item.Attribute("Click")?.Value == "OnRowEditClick");
        var rowCopy = document.Descendants(Xaml + "Button")
            .Single(item => item.Attribute("Click")?.Value == "OnRowCopyPrimaryClick");
        var detailEdit = RequireByName(document, "EditCipherButton");

        Assert.Contains("CanModify", rowEdit.Attribute("IsEnabled")?.Value);
        Assert.Contains("CanViewPassword", rowCopy.Attribute("IsEnabled")?.Value);
        Assert.Contains("CanModifySelectedItem", detailEdit.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void VaultPage_LoginTemplate_ShowsPasskeySummaryWhenPresent()
    {
        var document = LoadVaultPageXaml();
        var passkeyCard = document.Descendants(Xaml + "Border")
            .Single(element => element.Attribute("Visibility")?.Value?.Contains("HasPasskeys") == true);

        Assert.Contains(passkeyCard.Descendants(Xaml + "ItemsControl"),
            element => element.Attribute("ItemsSource")?.Value?.Contains("Passkeys") == true);
        Assert.Contains(passkeyCard.Descendants(Xaml + "TextBlock"),
            row => row.Attribute("Text")?.Value?.Contains("DisplaySummary") == true);
        Assert.Contains(passkeyCard.Descendants(Xaml + "TextBlock"),
            row => row.Attribute("Text")?.Value?.Contains("MetadataSummary") == true);

        var passkey = new App.ViewModels.Models.PasskeyDetail(
            "example.com", "account@example.com", "Example Account", true, null);
        Assert.Contains("example.com", passkey.DisplaySummary);
        Assert.Contains("Example Account", passkey.DisplaySummary);
        Assert.Contains("可发现", passkey.MetadataSummary);
    }

    [Fact]
    public void VaultPage_FolderComboBox_BindsToFolderFiltersAndSelectedFolder()
    {
        var document = LoadVaultPageXaml();
        var folderBox = RequireByName(document, "CipherEditorFolderBox");

        Assert.Contains("FolderFilters", folderBox.Attribute("ItemsSource")?.Value);
        // A compiled item template avoids reflective DisplayMemberPath and SelectedValuePath.
        Assert.Null(folderBox.Attribute("DisplayMemberPath"));
        Assert.Null(folderBox.Attribute("SelectedValuePath"));
        Assert.Contains("EditorSelectedFolder", folderBox.Attribute("SelectedItem")?.Value);
        // The typed template displays the current folder label.
        var templateText = folderBox.Descendants().First(e => e.Name.LocalName == "TextBlock");
        Assert.Contains("Label", templateText.Attribute("Text")?.Value);
        Assert.Equal("Left", folderBox.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("240", folderBox.Attribute("Width")?.Value);
        AssertFlatFieldLabel(folderBox, "文件夹");
    }

    [Fact]
    public void VaultPage_DestinationComboBox_UsesCompiledBindings()
    {
        var document = LoadVaultPageXaml();
        var destination = RequireByName(document, "CipherEditorDestinationBox");

        Assert.Contains("EditorDestinations", destination.Attribute("ItemsSource")?.Value);
        Assert.Contains("EditorSelectedDestination", destination.Attribute("SelectedItem")?.Value);
        Assert.Contains("CanChooseEditorDestination", destination.Attribute("IsEnabled")?.Value);
        Assert.Null(destination.Attribute("DisplayMemberPath"));
        var templateText = destination.Descendants().First(element => element.Name.LocalName == "TextBlock");
        Assert.Contains("DisplayName", templateText.Attribute("Text")?.Value);
    }

    [Fact]
    public void VaultPage_CustomFieldEditor_HasAddActionAndNameValueInputs()
    {
        var document = LoadVaultPageXaml();
        var addButton = RequireByName(document, "AddCustomFieldButton");
        var removeButton = RequireByName(document, "RemoveCustomFieldButton");
        var customFields = RequireByName(document, "CustomFieldsEditorItems");

        // AddCustomFieldButton uses x:Bind Command (non-template, AOT-safe)
        Assert.True(addButton.Attribute("Click") is not null || addButton.Attribute("Command") is not null);
        // RemoveCustomFieldButton uses Click handler (inside DataTemplate, AOT-safe)
        Assert.NotNull(removeButton.Attribute("Click"));
        Assert.Contains("EditorDisplayDraft.CustomFields", customFields.Attribute("ItemsSource")?.Value);

        var nameBox = customFields.Descendants(Xaml + "TextBox").Single(textBox =>
            textBox.Attribute("Text")?.Value?.Contains("Binding Name,") == true);
        var valueBox = RequireByName(document, "CustomFieldTextValueBox");
        Assert.Contains("Mode=TwoWay", nameBox.Attribute("Text")?.Value);
        Assert.Contains("TextValue, Mode=TwoWay", valueBox.Attribute("Text")?.Value);
        Assert.Equal("Stretch", nameBox.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("Stretch", valueBox.Attribute("HorizontalAlignment")?.Value);
        AssertFlatFieldLabel(nameBox, "名称");
        AssertFlatFieldLabel(valueBox, "值");
    }

    [Fact]
    public void VaultPage_TotpSecret_UsesPasswordBox()
    {
        var document = LoadVaultPageXaml();
        var editor = RequireByName(document, "TotpSecretEditor");

        Assert.Equal(Xaml + "PasswordBox", editor.Name);
        Assert.Contains("EditorDisplayDraft.Login.Totp", editor.Attribute("Password")?.Value);
        Assert.Contains("Mode=TwoWay", editor.Attribute("Password")?.Value);
        Assert.DoesNotContain(document.Descendants(Xaml + "TextBox"),
            textBox => textBox.Attribute("Text")?.Value?.Contains("EditorDisplayDraft.Login.Totp") == true);
    }

    [Fact]
    public void VaultPage_CustomFieldEditor_UsesTypeSpecificControls()
    {
        var document = LoadVaultPageXaml();
        var customFields = RequireByName(document, "CustomFieldsEditorItems");
        var typeBox = RequireByName(document, "CustomFieldTypeBox");
        var textValue = RequireByName(document, "CustomFieldTextValueBox");
        var hiddenValue = RequireByName(document, "CustomFieldHiddenValueBox");
        var booleanValue = RequireByName(document, "CustomFieldBooleanValueCheckBox");

        Assert.Null(typeBox.Attribute("SelectionChanged"));
        Assert.Contains("TypeIndex", typeBox.Attribute("SelectedIndex")?.Value);
        Assert.Contains("Mode=TwoWay", typeBox.Attribute("SelectedIndex")?.Value);
        Assert.Equal(
            new[] { "Text", "Hidden", "Boolean", "Linked", "Unsupported" },
            typeBox.Elements(Xaml + "ComboBoxItem").Select(item => item.Attribute("Tag")?.Value));
        Assert.All(
            typeBox.Elements(Xaml + "ComboBoxItem").Skip(3),
            item => Assert.Equal("False", item.Attribute("IsEnabled")?.Value));

        Assert.Equal(Xaml + "TextBox", textValue.Name);
        Assert.Contains("Value", textValue.Attribute("Text")?.Value);
        Assert.Contains("Mode=TwoWay", textValue.Attribute("Text")?.Value);
        Assert.Contains("IsText", textValue.Attribute("Visibility")?.Value);
        Assert.Contains("TextValue", textValue.Attribute("Text")?.Value);
        Assert.Null(textValue.Attribute(X + "Load"));

        Assert.Equal(Xaml + "PasswordBox", hiddenValue.Name);
        Assert.Contains("Value", hiddenValue.Attribute("Password")?.Value);
        Assert.Contains("Mode=TwoWay", hiddenValue.Attribute("Password")?.Value);
        Assert.Contains("IsHidden", hiddenValue.Attribute("Visibility")?.Value);
        Assert.Contains("HiddenValue", hiddenValue.Attribute("Password")?.Value);
        Assert.Null(hiddenValue.Attribute(X + "Load"));

        Assert.Equal(Xaml + "CheckBox", booleanValue.Name);
        Assert.Contains("BooleanValue", booleanValue.Attribute("IsChecked")?.Value);
        Assert.Contains("Mode=TwoWay", booleanValue.Attribute("IsChecked")?.Value);
        Assert.Contains("IsBoolean", booleanValue.Attribute("Visibility")?.Value);
        Assert.Null(booleanValue.Attribute(X + "Load"));

        Assert.DoesNotContain("{Binding ElementName=Root", customFields.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VaultPage_EditorInputs_StretchExceptCompactPlacementPickers()
    {
        var document = LoadVaultPageXaml();
        var editorPanel = RequireByName(document, "CipherEditorPanel");

        AssertStretchInputs(editorPanel, "TextBox");
        AssertStretchInputs(editorPanel, "PasswordBox");
        AssertStretchInputs(editorPanel, "ComboBox");
    }

    [Fact]
    public void VaultPage_EditorPanel_DoesNotNestScrollViewer()
    {
        var document = LoadVaultPageXaml();
        var editorPanel = RequireByName(document, "CipherEditorPanel");

        Assert.NotEqual(Xaml + "ScrollViewer", editorPanel.Name);
        Assert.Empty(editorPanel.Descendants(Xaml + "ScrollViewer"));
    }

    private static XElement? FindByName(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(element => element.Attribute(X + "Name")?.Value == name);

    private static XElement RequireByName(XDocument document, string name)
    {
        var element = FindByName(document, name);
        if (element is null)
        {
            Assert.Fail($"Expected x:Name='{name}' in VaultPage.xaml.");
            throw new InvalidOperationException($"Expected x:Name='{name}' in VaultPage.xaml.");
        }

        return element;
    }

    private static void AssertStretchInputs(XElement editorPanel, string controlName)
    {
        var inputs = editorPanel.Descendants(Xaml + controlName).ToArray();

        Assert.NotEmpty(inputs);
        Assert.All(inputs, input =>
        {
            if (input.Attribute(X + "Name")?.Value is "CipherEditorDestinationBox" or "CipherEditorFolderBox")
            {
                Assert.Equal("Left", input.Attribute("HorizontalAlignment")?.Value);
                Assert.Equal("240", input.Attribute("Width")?.Value);
                Assert.Equal("1", input.Attribute("Grid.Column")?.Value);
            }
            else
                Assert.Equal("Stretch", input.Attribute("HorizontalAlignment")?.Value);
        });
    }

    private static void AssertFlatFieldLabel(XElement input, string label)
    {
        Assert.Null(input.Attribute("Header"));
        Assert.Equal("1", input.Attribute("Grid.Column")?.Value);
        Assert.Contains(input.Parent!.Elements(Xaml + "TextBlock"), element => element.Attribute("Text")?.Value == label);
    }

    private static XDocument LoadVaultPageXaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return XDocument.Load(path);

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find VaultPage.xaml from the test output directory.");
    }

    [Fact]
    public void VaultPage_DetailActions_AreNamedAndWired()
    {
        var document = LoadVaultPageXaml();

        Assert.Equal("OnEditCipherClick", RequireByName(document, "EditCipherButton").Attribute("Click")?.Value);
        Assert.Equal("OnDeleteCipherClick", RequireByName(document, "DeleteCipherButton").Attribute("Click")?.Value);
        Assert.Equal("OnRestoreCipherClick", RequireByName(document, "RestoreCipherButton").Attribute("Click")?.Value);
        Assert.Equal("OnPermanentDeleteCipherClick", RequireByName(document, "PermanentDeleteCipherButton").Attribute("Click")?.Value);
        Assert.Equal("OnRenameFolderClick", RequireByName(document, "RenameFolderButton").Attribute("Click")?.Value);
        Assert.Equal("OnDeleteFolderClick", RequireByName(document, "DeleteFolderButton").Attribute("Click")?.Value);
    }

    [Fact]
    public void VaultPage_SaveButton_StaysWiredToSaveHandler()
    {
        var document = LoadVaultPageXaml();
        Assert.Equal("OnSaveCipherEditorClick", RequireByName(document, "SaveCipherEditorButton").Attribute("Click")?.Value);
    }

    [Fact]
    public void VaultPage_CustomFieldButtons_AreAotSafe()
    {
        var document = LoadVaultPageXaml();
        var addButton = RequireByName(document, "AddCustomFieldButton");
        var removeButton = RequireByName(document, "RemoveCustomFieldButton");

        // AddCustomFieldButton is outside a DataTemplate — uses x:Bind Command directly (AOT-safe).
        Assert.Null(addButton.Attribute("Click"));
        Assert.Contains("AddCustomFieldCommand", addButton.Attribute("Command")?.Value);

        // RemoveCustomFieldButton is inside a DataTemplate — uses Click handler (AOT-safe; was {Binding ElementName=Root}).
        Assert.Null(removeButton.Attribute("Command"));
        Assert.NotNull(removeButton.Attribute("Click"));
        Assert.Null(removeButton.Attribute("CommandParameter"));
    }

    [Fact]
    public void VaultPage_EditorPanel_UsesXBindNotClassicBinding()
    {
        var document = LoadVaultPageXaml();
        var panel = RequireByName(document, "CipherEditorPanel");

        // No DataContext shim on the panel anymore.
        Assert.Null(panel.Attribute("DataContext"));

        // Editable fields bind via x:Bind to ViewModel.EditorDisplayDraft.
        var nameBox = RequireByName(document, "CipherEditorNameBox");
        Assert.Contains(panel, nameBox.Ancestors());
        Assert.Contains("x:Bind", nameBox.Attribute("Text")?.Value);
        Assert.Contains("ViewModel.EditorDisplayDraft.Name", nameBox.Attribute("Text")?.Value);
        Assert.Contains("TwoWay", nameBox.Attribute("Text")?.Value);
    }

    [Fact]
    public void VaultPage_LoginUrisPreserveEveryWebsiteAndMatchingRule()
    {
        var document = LoadVaultPageXaml();
        var panel = RequireByName(document, "CipherEditorPanel");

        var uriItems = panel.Descendants(Xaml + "ItemsControl").Single(element =>
            element.Attribute("ItemsSource")?.Value?.Contains("EditorDisplayDraft.Login.Uris") == true);
        var uriBox = uriItems.Descendants(Xaml + "TextBox")
            .Single(tb => tb.Attribute("Text")?.Value?.Contains("Binding Uri,") == true);
        AssertFlatFieldLabel(uriBox, "网站地址");
        Assert.Contains("Binding Uri, Mode=TwoWay", uriBox.Attribute("Text")?.Value);
        Assert.DoesNotContain("Uris[0]", uriBox.Attribute("Text")?.Value);
        var matching = uriItems.Descendants(Xaml + "Expander").Single();
        Assert.Contains("MatchSummary", matching.Attribute("Header")?.Value);
        Assert.NotEqual("True", matching.Attribute("IsExpanded")?.Value);
        Assert.Contains(matching.Descendants(Xaml + "ComboBox"), element =>
            element.Attribute("SelectedIndex")?.Value?.Contains("MatchIndex") == true);
    }

    [Fact]
    public void VaultPage_CustomFieldTemplate_HasXDataType()
    {
        var document = LoadVaultPageXaml();
        var items = RequireByName(document, "CustomFieldsEditorItems");
        var template = items.Descendants(Xaml + "DataTemplate").First();

        Assert.Contains("CustomFieldEditorDraft", template.Attribute(X + "DataType")?.Value);
    }
}
