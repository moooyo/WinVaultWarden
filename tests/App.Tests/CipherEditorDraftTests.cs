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

    [Fact]
    public void ChangingType_RaisesTypeFlagPropertyChangedNotifications()
    {
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        var changedProperties = new HashSet<string>();

        draft.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
                changedProperties.Add(args.PropertyName);
        };

        draft.Type = VaultItemKind.Card;

        Assert.Contains(nameof(CipherEditorDraft.IsLogin), changedProperties);
        Assert.Contains(nameof(CipherEditorDraft.IsCard), changedProperties);
        Assert.Contains(nameof(CipherEditorDraft.IsIdentity), changedProperties);
        Assert.Contains(nameof(CipherEditorDraft.IsSecureNote), changedProperties);
        Assert.Contains(nameof(CipherEditorDraft.IsSshKey), changedProperties);
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
    public void Validate_SshAllowsAllKeyFieldsWhenNameExists()
    {
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Ssh);
        draft.Name = "prod ssh";
        draft.SshKey.PrivateKey = "private";
        draft.SshKey.PublicKey = "ssh-ed25519 AAAA";
        draft.SshKey.KeyFingerprint = "SHA256:abc";

        var errors = draft.Validate();

        Assert.Empty(errors);
        Assert.True(draft.HasRequiredData());
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

    [Fact]
    public void PrimaryUri_Get_ReturnsFirstUri()
    {
        var login = new LoginEditorDraft();
        login.Uris[0].Uri = "https://example.com";

        Assert.Equal("https://example.com", login.PrimaryUri);
    }

    [Fact]
    public void PrimaryUri_Set_WritesFirstUri_AndRaisesChange()
    {
        var login = new LoginEditorDraft();
        var raised = new List<string?>();
        login.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        login.PrimaryUri = "https://set.example";

        Assert.Equal("https://set.example", login.Uris[0].Uri);
        Assert.Single(login.Uris);
        Assert.Contains(nameof(LoginEditorDraft.PrimaryUri), raised);
    }

    [Fact]
    public void PrimaryUri_Set_WhenUrisEmpty_SeedsOneEntry()
    {
        var login = new LoginEditorDraft();
        login.Uris.Clear();

        login.PrimaryUri = "https://seeded";

        Assert.Single(login.Uris);
        Assert.Equal("https://seeded", login.Uris[0].Uri);
        Assert.Equal("https://seeded", login.PrimaryUri);
    }

    [Fact]
    public void PrimaryUri_Get_WhenUrisEmpty_SeedsAndReturnsEmpty()
    {
        var login = new LoginEditorDraft();
        login.Uris.Clear();

        Assert.Equal(string.Empty, login.PrimaryUri);
        Assert.Single(login.Uris);
    }
}
