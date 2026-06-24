using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class CipherEditorDraftTests
{
    [Fact]
    public void CreateDefault_Login_InitializesCommonAndTypeDrafts()
    {
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);

        Assert.Equal(VaultItemKind.Login, draft.Type);
        Assert.True(draft.IsLogin);
        Assert.False(draft.IsCard);
        Assert.False(draft.Favorite);
        Assert.False(draft.Reprompt);
        Assert.NotNull(draft.Login);
        Assert.NotNull(draft.Card);
        Assert.NotNull(draft.Identity);
        Assert.NotNull(draft.SecureNote);
        Assert.NotNull(draft.SshKey);
        Assert.Single(draft.Login.Uris);
        Assert.Equal(0, draft.SecureNote.Type);
    }

    [Fact]
    public void ChangingType_PreservesCommonFieldsAndTypeDrafts()
    {
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        draft.Name = "Production";
        draft.Notes = "private note";
        draft.Login.Username = "admin";

        draft.Type = VaultItemKind.Ssh;
        draft.SshKey.PublicKey = "ssh-ed25519 AAAA";
        draft.Type = VaultItemKind.Login;

        Assert.Equal("Production", draft.Name);
        Assert.Equal("private note", draft.Notes);
        Assert.Equal("admin", draft.Login.Username);
        Assert.Equal("ssh-ed25519 AAAA", draft.SshKey.PublicKey);
        Assert.True(draft.IsLogin);
    }

    [Theory]
    [InlineData(VaultItemKind.Login)]
    [InlineData(VaultItemKind.Card)]
    [InlineData(VaultItemKind.Identity)]
    [InlineData(VaultItemKind.Note)]
    [InlineData(VaultItemKind.Ssh)]
    public void Validate_RequiresNameForEveryType(VaultItemKind type)
    {
        var draft = CipherEditorDraft.CreateDefault(type);

        var errors = draft.Validate();

        Assert.Contains("项目名称为必填项。", errors);
        Assert.False(draft.HasRequiredData());
    }

    [Fact]
    public void Validate_SshRequiresAllKeyFields()
    {
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Ssh);
        draft.Name = "prod ssh";

        var errors = draft.Validate();

        Assert.Contains("SSH 私钥为必填项。", errors);
        Assert.Contains("SSH 公钥为必填项。", errors);
        Assert.Contains("SSH 指纹为必填项。", errors);
        Assert.False(draft.HasRequiredData());
    }

    [Fact]
    public void Validate_LoginAllowsEmptyCredentialFieldsWhenNameExists()
    {
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        draft.Name = "empty login";

        var errors = draft.Validate();

        Assert.Empty(errors);
        Assert.True(draft.HasRequiredData());
    }

    [Fact]
    public void CustomFields_DefaultToTextAndCanStoreHiddenValues()
    {
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        var field = new CustomFieldEditorDraft();

        Assert.Equal(CipherEditorFieldType.Text, field.Type);

        field.Name = "Recovery";
        field.Type = CipherEditorFieldType.Hidden;
        field.Value = "secret";

        draft.CustomFields.Add(field);

        Assert.Single(draft.CustomFields);
        Assert.Equal(CipherEditorFieldType.Hidden, draft.CustomFields[0].Type);
        Assert.Equal("secret", draft.CustomFields[0].Value);
    }
}
