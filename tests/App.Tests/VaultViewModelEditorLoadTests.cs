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
            "IsEditing=false (x:Load unload) must precede EditorDraft=null");
    }
}
