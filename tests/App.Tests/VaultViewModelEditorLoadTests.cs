using System.Collections.Generic;
using System.ComponentModel;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class VaultViewModelEditorLoadTests
{
    private static VaultViewModel NewVm() => new(new MockVaultUiService());

    [Fact]
    public void NewDraftTerminatesSynchronousDestinationAndTypeBindingFeedback()
    {
        var vm = NewVm();
        var destinationNotifications = 0;
        var titleNotifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VaultViewModel.EditorSelectedDestination))
            {
                Assert.True(++destinationNotifications <= 4, "Destination binding must converge.");
                vm.EditorSelectedDestination = vm.EditorSelectedDestination;
            }
            if (e.PropertyName == nameof(VaultViewModel.EditorTitle))
            {
                Assert.True(++titleNotifications <= 2, "Type binding must converge.");
                vm.ChangeEditorType(VaultItemKind.Login);
            }
        };

        vm.BeginAdd(VaultItemKind.Login);

        Assert.True(vm.IsEditing);
        Assert.NotNull(vm.EditorDraft);
        Assert.Empty(vm.EditorDraft.Name);
        Assert.Null(vm.EditorDraft.OrganizationId);
        Assert.Empty(vm.EditorCollectionOptions);
        var options = vm.EditorCollectionOptions;
        vm.EditorSelectedDestination = vm.EditorSelectedDestination;
        vm.ChangeEditorType(VaultItemKind.Login);
        Assert.Same(options, vm.EditorCollectionOptions);
    }

    [Fact]
    public void BindingRefreshCannotResetExistingFolderOrOwner()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        vm.EditorDraft!.FolderId = "f1";
        vm.EditorDraft.OrganizationId = "organization-a";

        vm.RefreshEditorBindings(() =>
        {
            vm.EditorSelectedFolder = null;
            vm.EditorSelectedDestination = CipherDestinationOption.Personal;
        });

        Assert.Equal("f1", vm.EditorDraft.FolderId);
        Assert.Equal("organization-a", vm.EditorDraft.OrganizationId);
        vm.EditorSelectedFolder = null;
        Assert.Null(vm.EditorDraft.FolderId);
    }

    [Fact]
    public void CustomFieldTypeChangesDoNotLeaveASecretInThePlainTextProjection()
    {
        var field = new CustomFieldEditorDraft { Type = CipherEditorFieldType.Hidden, Value = "protected" };
        Assert.Empty(field.TextValue);
        Assert.Equal("protected", field.HiddenValue);
        field.TextValue = "inactive control";
        Assert.Equal("protected", field.Value);

        field.Type = CipherEditorFieldType.Text;
        Assert.Equal("protected", field.TextValue);
        Assert.Empty(field.HiddenValue);
        field.HiddenValue = "inactive secret control";
        Assert.Equal("protected", field.Value);

        field.Type = CipherEditorFieldType.Hidden;
        Assert.Empty(field.TextValue);
        Assert.Equal("protected", field.HiddenValue);
    }

    [Fact]
    public void CancelEdit_ClearsEditingThenDraft()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        Assert.True(vm.IsEditing);
        Assert.NotNull(vm.EditorDraft);

        vm.CancelEdit();

        Assert.False(vm.IsEditing);
        Assert.Null(vm.EditorDraft);
    }

    [Fact]
    public void CancelEdit_RaisesIsEditingBeforeEditorDraft()
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);

        var order = new List<string>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(VaultViewModel.IsEditing)
                or nameof(VaultViewModel.EditorDraft))
            {
                order.Add(e.PropertyName!);
            }
        };

        vm.CancelEdit();

        var idxEditing = order.IndexOf(nameof(VaultViewModel.IsEditing));
        var idxDraft = order.IndexOf(nameof(VaultViewModel.EditorDraft));
        Assert.True(idxEditing >= 0, "IsEditing change must be notified");
        Assert.True(idxDraft >= 0, "EditorDraft change must be notified");
        Assert.True(idxEditing < idxDraft,
            "The editor must be hidden before its business draft is discarded.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscardingDraftReplacesEveryPresentationSecretWithAnEmptySource(bool lockVault)
    {
        var vm = NewVm();
        vm.BeginAdd(VaultItemKind.Login);
        var draft = vm.EditorDraft!;
        draft.Name = "Sensitive item";
        draft.Notes = "Sensitive note";
        draft.Login.Password = "password";
        draft.Login.Totp = "totp";
        draft.Identity.PassportNumber = "passport";
        draft.Card.Number = "card";
        draft.SshKey.PrivateKey = "private key";
        draft.BankAccount.Pin = "pin";
        draft.DriversLicense.LicenseNumber = "license";
        draft.Passport.PassportNumber = "passport number";
        draft.CustomFields.Add(new CustomFieldEditorDraft { Name = "secret", Value = "hidden" });
        Assert.Same(draft, vm.EditorDisplayDraft);

        if (lockVault) vm.ClearSensitive();
        else vm.CancelEdit();

        Assert.Null(vm.EditorDraft);
        Assert.False(vm.IsEditing);
        Assert.NotSame(draft, vm.EditorDisplayDraft);
        Assert.Empty(vm.EditorDisplayDraft.Name);
        Assert.Empty(vm.EditorDisplayDraft.Notes);
        Assert.Empty(vm.EditorDisplayDraft.Login.Password);
        Assert.Empty(vm.EditorDisplayDraft.Login.Totp);
        Assert.Empty(vm.EditorDisplayDraft.Identity.PassportNumber);
        Assert.Empty(vm.EditorDisplayDraft.Card.Number);
        Assert.Empty(vm.EditorDisplayDraft.SshKey.PrivateKey);
        Assert.Empty(vm.EditorDisplayDraft.BankAccount.Pin);
        Assert.Empty(vm.EditorDisplayDraft.DriversLicense.LicenseNumber);
        Assert.Empty(vm.EditorDisplayDraft.Passport.PassportNumber);
        Assert.Empty(vm.EditorDisplayDraft.CustomFields);

        if (!lockVault)
        {
            vm.BeginAdd(VaultItemKind.Login);
            Assert.Same(vm.EditorDraft, vm.EditorDisplayDraft);
            Assert.Empty(vm.EditorDisplayDraft.Login.Password);
        }
    }

    [Fact]
    public async Task EmptyPresentationSourceCannotBecomeABusinessDraft()
    {
        var vm = NewVm();
        var itemCount = vm.Items.Count;
        vm.EditorDisplayDraft.Name = "Presentation-only value";
        vm.EditorDisplayDraft.Login.Password = "Presentation-only secret";

        var result = await vm.SaveDraftAsync();

        Assert.False(result.IsSuccess);
        Assert.Null(vm.EditorDraft);
        Assert.False(vm.IsEditing);
        Assert.Equal(itemCount, vm.Items.Count);
        vm.ClearSensitive();
        Assert.Empty(vm.EditorDisplayDraft.Name);
        Assert.Empty(vm.EditorDisplayDraft.Login.Password);
    }
}
