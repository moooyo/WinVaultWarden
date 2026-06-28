using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class CipherEditorXamlTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void VaultPage_AddMenu_ContainsCipherTypesAndFolder()
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
        Assert.Contains("文件夹", flyoutItems);
    }

    [Fact]
    public void VaultPage_EditorActions_HaveStableNames()
    {
        var document = LoadVaultPageXaml();

        RequireByName(document, "SaveCipherEditorButton");
        RequireByName(document, "CancelCipherEditorButton");
        RequireByName(document, "CipherEditorPanel");
        RequireByName(document, "CipherEditorTypeBox");
        RequireByName(document, "CipherEditorFolderBox");
        RequireByName(document, "AddCustomFieldButton");
        RequireByName(document, "RemoveCustomFieldButton");
        RequireByName(document, "CustomFieldsEditorItems");
        RequireByName(document, "AddFolderMenuItem");
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
    public void VaultPage_FolderComboBox_BindsToFolderFiltersAndDraftFolderId()
    {
        var document = LoadVaultPageXaml();
        var folderBox = RequireByName(document, "CipherEditorFolderBox");

        Assert.Contains("FolderFilters", folderBox.Attribute("ItemsSource")?.Value);
        Assert.Equal("Label", folderBox.Attribute("DisplayMemberPath")?.Value);
        Assert.Equal("FolderId", folderBox.Attribute("SelectedValuePath")?.Value);
        Assert.Contains("EditorDraft.FolderId", folderBox.Attribute("SelectedValue")?.Value);
        Assert.Equal("Stretch", folderBox.Attribute("HorizontalAlignment")?.Value);
    }

    [Fact]
    public void VaultPage_CustomFieldEditor_HasAddActionAndNameValueInputs()
    {
        var document = LoadVaultPageXaml();
        var addButton = RequireByName(document, "AddCustomFieldButton");
        var removeButton = RequireByName(document, "RemoveCustomFieldButton");
        var customFields = RequireByName(document, "CustomFieldsEditorItems");

        Assert.True(addButton.Attribute("Click") is not null || addButton.Attribute("Command") is not null);
        Assert.Contains("RemoveCustomFieldCommand", removeButton.Attribute("Command")?.Value);
        Assert.Equal("{x:Bind}", removeButton.Attribute("CommandParameter")?.Value);
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
    public void VaultPage_CustomFieldButtons_UseCommandsNotClickHandlers()
    {
        var document = LoadVaultPageXaml();
        var addButton = RequireByName(document, "AddCustomFieldButton");
        var removeButton = RequireByName(document, "RemoveCustomFieldButton");

        Assert.Null(addButton.Attribute("Click"));
        Assert.Contains("AddCustomFieldCommand", addButton.Attribute("Command")?.Value);

        Assert.Null(removeButton.Attribute("Click"));
        Assert.Contains("RemoveCustomFieldCommand", removeButton.Attribute("Command")?.Value);
        Assert.Equal("{x:Bind}", removeButton.Attribute("CommandParameter")?.Value);
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
