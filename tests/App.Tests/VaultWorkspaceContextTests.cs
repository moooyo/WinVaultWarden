using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public sealed class VaultWorkspaceContextTests
{
    [Fact]
    public void EditorValidityTracksRequiredFieldsAndDetachesDiscardedDrafts()
    {
        var viewModel = new VaultViewModel(new MockVaultUiService());
        viewModel.BeginAdd(VaultItemKind.Ssh);
        var draft = viewModel.EditorDraft!;
        Assert.False(viewModel.HasValidEditorDraft);

        draft.Name = "Server";
        draft.SshKey.PrivateKey = "private";
        draft.SshKey.PublicKey = "public";
        draft.SshKey.KeyFingerprint = "fingerprint";
        Assert.True(viewModel.HasValidEditorDraft);

        viewModel.CancelEdit();
        draft.Name = "discarded";

        Assert.False(viewModel.HasValidEditorDraft);
        Assert.Null(viewModel.EditorDraft);
    }

    [Fact]
    public void SavedViewRoundTripRetainsTheTypeRefinement()
    {
        var viewModel = new VaultViewModel(new MockVaultUiService()) { SelectedTypeIndex = 3 };
        var persisted = SavedSearchViewData.FromView(viewModel.CaptureCurrentView("Identity"));
        viewModel.SelectedTypeIndex = 0;

        viewModel.ApplyViewCommand.Execute(persisted.ToView());

        Assert.Equal(3, viewModel.SelectedTypeIndex);
    }

    [Fact]
    public void ReturningToResultsPreservesRefinementsAndPositionWithoutOpeningACredential()
    {
        var viewModel = new VaultViewModel(new MockVaultUiService());
        var item = viewModel.FilteredItems.First();
        viewModel.SearchText = item.Name;
        viewModel.SelectedSort = VaultSortKey.RevisionDesc;
        viewModel.SelectedItem = viewModel.FilteredItems.Single();
        viewModel.RememberWorkspaceContext(241, item.Id, searchHadFocus: false);

        viewModel.ReturnToResults();

        Assert.Null(viewModel.SelectedItem);
        Assert.Null(viewModel.Detail);
        Assert.Equal(item.Name, viewModel.SearchText);
        Assert.Equal(VaultSortKey.RevisionDesc, viewModel.SelectedSort);
        Assert.Equal(241, viewModel.WorkspaceContext.VerticalOffset);
        Assert.Equal(item.Id, viewModel.WorkspaceContext.FocusedItemId);
    }

    [Fact]
    public void DifferentScopesKeepIndependentSearchTypeFacetsAndFocus()
    {
        var viewModel = new VaultViewModel(new MockVaultUiService());
        viewModel.SearchText = "work";
        viewModel.SelectedTypeIndex = 1;
        viewModel.FacetTotp = true;
        viewModel.SelectedSort = VaultSortKey.CreationDesc;
        viewModel.RememberWorkspaceContext(180, null, searchHadFocus: true);

        viewModel.SelectFilterByTag("vault:folder:f1");

        Assert.Empty(viewModel.SearchText);
        Assert.Equal(0, viewModel.SelectedTypeIndex);
        Assert.False(viewModel.FacetTotp);
        Assert.Equal(0, viewModel.WorkspaceContext.VerticalOffset);
        viewModel.SearchText = "folder";
        viewModel.FacetAttachment = true;
        viewModel.RememberWorkspaceContext(44, "folder-item", searchHadFocus: false);

        viewModel.SelectFilterByTag("vault:allitems");

        Assert.Equal("work", viewModel.SearchText);
        Assert.Equal(1, viewModel.SelectedTypeIndex);
        Assert.True(viewModel.FacetTotp);
        Assert.False(viewModel.FacetAttachment);
        Assert.Equal(VaultSortKey.CreationDesc, viewModel.SelectedSort);
        Assert.Equal(180, viewModel.WorkspaceContext.VerticalOffset);
        Assert.True(viewModel.WorkspaceContext.SearchHadFocus);
        Assert.Null(viewModel.Detail);

        viewModel.SelectFilterByTag("vault:folder:f1");

        Assert.Equal("folder", viewModel.SearchText);
        Assert.True(viewModel.FacetAttachment);
        Assert.Equal(44, viewModel.WorkspaceContext.VerticalOffset);
    }

    [Fact]
    public void LockClearsAllRememberedScopesAndRejectsLatePositionCapture()
    {
        var viewModel = new VaultViewModel(new MockVaultUiService());
        viewModel.SearchText = "account-specific-search";
        viewModel.FacetFavoriteOnly = true;
        viewModel.SelectedTypeIndex = 2;
        viewModel.RememberWorkspaceContext(300, "account-item", searchHadFocus: true);
        viewModel.SelectFilterByTag("vault:folder:f1");
        viewModel.SearchText = "private-folder-search";

        viewModel.ClearSensitive();
        viewModel.RememberWorkspaceContext(900, "late-item", searchHadFocus: true);
        viewModel.RefreshFromSnapshot();
        viewModel.SelectFilterByTag("vault:folder:f1");
        viewModel.SelectFilterByTag("vault:allitems");

        Assert.Empty(viewModel.SearchText);
        Assert.False(viewModel.CurrentFacets.Any);
        Assert.Equal(0, viewModel.SelectedTypeIndex);
        Assert.Equal(0, viewModel.WorkspaceContext.VerticalOffset);
        Assert.Null(viewModel.WorkspaceContext.FocusedItemId);
        Assert.Null(viewModel.SelectedItem);
        Assert.Null(viewModel.Detail);
    }

    [Fact]
    public void DirectItemNavigationClearsIncompatibleFiltersAndSelectsTheExactId()
    {
        var viewModel = new VaultViewModel(new MockVaultUiService());
        var item = viewModel.FilteredItems.First();
        viewModel.SearchText = "no such item";
        viewModel.SelectedTypeIndex = 8;
        viewModel.FacetAttachment = true;

        viewModel.RestoreWorkspaceContext($"vault:item:{item.Id}");

        Assert.Equal(item.Id, viewModel.SelectedItem?.Id);
        Assert.Equal(item.Id, viewModel.Detail?.Id);
        Assert.Empty(viewModel.SearchText);
        Assert.Equal(0, viewModel.SelectedTypeIndex);
        Assert.False(viewModel.CurrentFacets.Any);

        viewModel.RestoreWorkspaceContext("vault:item:missing");

        Assert.Null(viewModel.SelectedItem);
        Assert.Null(viewModel.Detail);
        Assert.True(viewModel.HasOperationError);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    [InlineData(5, 6)]
    public void WebsiteMatchingPickerPreservesProtocolValues(int? match, int index)
    {
        var uri = new LoginUriEditorDraft { Uri = "https://example.test", Match = match };

        Assert.Equal(index, uri.MatchIndex);
        uri.MatchIndex = index;

        Assert.Equal(match, uri.Match);
    }

    [Fact]
    public void UnknownWebsiteMatchingRuleIsNotReplacedByAnUnselectedPicker()
    {
        var uri = new LoginUriEditorDraft { Match = 42 };

        Assert.Equal(-1, uri.MatchIndex);
        uri.MatchIndex = -1;

        Assert.Equal(42, uri.Match);
        Assert.Contains("42", uri.MatchSummary);
    }
}
