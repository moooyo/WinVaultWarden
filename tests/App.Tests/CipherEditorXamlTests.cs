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
        var passkeyCard = document.Descendants()
            .FirstOrDefault(element => element.Attribute("Title")?.Value == "Passkey");

        Assert.NotNull(passkeyCard);
        Assert.Contains("HasPasskeys", passkeyCard!.Attribute("Visibility")?.Value);
        Assert.Contains(passkeyCard.Descendants().Where(element => element.Name.LocalName == "FieldRow"),
            row => row.Attribute("Label")?.Value == "RP ID" && row.Attribute("Value")?.Value?.Contains("RpId") == true);
        Assert.Contains(passkeyCard.Descendants().Where(element => element.Name.LocalName == "FieldRow"),
            row => row.Attribute("Value")?.Value?.Contains("DisplayName") == true);
    }

    [Fact]
    public void VaultPage_FolderComboBox_BindsToFolderFiltersAndSelectedFolder()
    {
        var document = LoadVaultPageXaml();
        var folderBox = RequireByName(document, "CipherEditorFolderBox");

        Assert.Contains("FolderFilters", folderBox.Attribute("ItemsSource")?.Value);
        // AOT 安全:用编译绑定的 ItemTemplate + SelectedItem 取代反射式 DisplayMemberPath/SelectedValuePath。
        Assert.Null(folderBox.Attribute("DisplayMemberPath"));
        Assert.Null(folderBox.Attribute("SelectedValuePath"));
        Assert.Contains("EditorSelectedFolder", folderBox.Attribute("SelectedItem")?.Value);
        // ItemTemplate 用 x:Bind Label 展示文件夹名(编译绑定,无反射)。
        var templateText = folderBox.Descendants().First(e => e.Name.LocalName == "TextBlock");
        Assert.Contains("Label", templateText.Attribute("Text")?.Value);
        Assert.Equal("Stretch", folderBox.Attribute("HorizontalAlignment")?.Value);
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
        Assert.Contains("EditorDraft.CustomFields", customFields.Attribute("ItemsSource")?.Value);

        var textBoxes = customFields.Descendants(Xaml + "TextBox").ToArray();
        Assert.Contains(textBoxes, textBox => textBox.Attribute("Header")?.Value == "名称"
            && textBox.Attribute("Text")?.Value?.Contains("Name") == true
            && textBox.Attribute("HorizontalAlignment")?.Value == "Stretch");
        Assert.Contains(textBoxes, textBox => textBox.Attribute("Header")?.Value == "值"
            && textBox.Attribute("Text")?.Value?.Contains("Value") == true
            && textBox.Attribute("HorizontalAlignment")?.Value == "Stretch");
    }

    [Fact]
    public void VaultPage_TotpSecret_UsesPasswordBox()
    {
        var document = LoadVaultPageXaml();
        var editor = RequireByName(document, "TotpSecretEditor");

        Assert.Equal(Xaml + "PasswordBox", editor.Name);
        Assert.Contains("EditorDraft.Login.Totp", editor.Attribute("Password")?.Value);
        Assert.Contains("Mode=TwoWay", editor.Attribute("Password")?.Value);
        Assert.DoesNotContain(document.Descendants(Xaml + "TextBox"),
            textBox => textBox.Attribute("Text")?.Value?.Contains("EditorDraft.Login.Totp") == true);
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
        Assert.Contains("IsText", textValue.Attribute(X + "Load")?.Value);
        Assert.Contains("Mode=OneWay", textValue.Attribute(X + "Load")?.Value);

        Assert.Equal(Xaml + "PasswordBox", hiddenValue.Name);
        Assert.Contains("Value", hiddenValue.Attribute("Password")?.Value);
        Assert.Contains("Mode=TwoWay", hiddenValue.Attribute("Password")?.Value);
        Assert.Contains("IsHidden", hiddenValue.Attribute(X + "Load")?.Value);
        Assert.Contains("Mode=OneWay", hiddenValue.Attribute(X + "Load")?.Value);

        Assert.Equal(Xaml + "CheckBox", booleanValue.Name);
        Assert.Contains("BooleanValue", booleanValue.Attribute("IsChecked")?.Value);
        Assert.Contains("Mode=TwoWay", booleanValue.Attribute("IsChecked")?.Value);
        Assert.Contains("IsBoolean", booleanValue.Attribute(X + "Load")?.Value);
        Assert.Contains("Mode=OneWay", booleanValue.Attribute(X + "Load")?.Value);

        Assert.DoesNotContain("{Binding ", customFields.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VaultPage_EditorInputs_StretchHorizontally()
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
        Assert.All(inputs, input => Assert.Equal("Stretch", input.Attribute("HorizontalAlignment")?.Value));
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

        // Editable fields bind via x:Bind to ViewModel.EditorDraft.
        var nameBox = panel.Descendants(Xaml + "TextBox")
            .First(tb => tb.Attribute("Header")?.Value == "项目名称（必填）");
        Assert.Contains("x:Bind", nameBox.Attribute("Text")?.Value);
        Assert.Contains("ViewModel.EditorDraft.Name", nameBox.Attribute("Text")?.Value);
        Assert.Contains("TwoWay", nameBox.Attribute("Text")?.Value);
    }

    [Fact]
    public void VaultPage_LoginUri_BindsToPrimaryUriProxy()
    {
        var document = LoadVaultPageXaml();
        var panel = RequireByName(document, "CipherEditorPanel");

        var uriBox = panel.Descendants(Xaml + "TextBox")
            .First(tb => tb.Attribute("Header")?.Value == "网站（URI）");
        Assert.Contains("ViewModel.EditorDraft.Login.PrimaryUri", uriBox.Attribute("Text")?.Value);
        Assert.DoesNotContain("Uris[0]", uriBox.Attribute("Text")?.Value);
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
