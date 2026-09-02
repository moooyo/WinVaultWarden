using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public sealed class VaultOrganizationMetadataTests
{
    [Fact]
    public async Task OrganizationItem_CanBeFavoritedAndMovedWithoutEnablingContentEdits()
    {
        var service = new OrganizationItemUiService();
        var viewModel = new VaultViewModel(service);
        var item = Assert.Single(viewModel.Items);
        viewModel.SelectedItem = item;

        Assert.False(item.CanModify);
        Assert.True(item.CanFavorite);
        Assert.False(viewModel.CanModifySelectedItem);

        var favorite = await viewModel.ToggleFavoriteAsync(item.Id);

        Assert.True(favorite.IsSuccess);
        Assert.True(service.Favorite);
        Assert.True(Assert.Single(viewModel.Items).Favorite);

        viewModel.IsSelectionMode = true;
        viewModel.SetItemSelected(item.Id, selected: true);
        Assert.False(viewModel.CanModifySelection);
        Assert.False(viewModel.CanDeleteSelection);
        Assert.True(viewModel.CanMoveSelection);

        await viewModel.MoveSelectedToFolderAsync("folder-1");

        Assert.Equal(new[] { item.Id }, service.LastMovedIds);
        Assert.Equal("folder-1", service.LastFolderId);
    }

    private sealed class OrganizationItemUiService : IVaultUiService
    {
        private const string ItemId = "org-item";

        public bool Favorite { get; private set; }
        public IReadOnlyCollection<string>? LastMovedIds { get; private set; }
        public string? LastFolderId { get; private set; }
        public int SkippedCipherCount => 0;
        public bool DataStale => false;

        public IReadOnlyList<CipherListItem> GetItems() =>
        [
            new CipherListItem
            {
                Id = ItemId,
                Name = "Shared login",
                Kind = VaultItemKind.Login,
                OrganizationId = "org-1",
                CanEdit = false,
                CanDelete = false,
                CanRestore = false,
                Favorite = Favorite,
                SearchHaystack = "shared login",
            },
        ];

        public CipherAccessPolicy GetAccessPolicy(string id) => new(id, false);

        public CipherDetail GetDetail(string id, bool includeSensitive) => new LoginDetail
        {
            Id = id,
            Name = "Shared login",
            CanEdit = false,
            Favorite = Favorite,
            SensitiveUnlocked = true,
        };

        public IReadOnlyList<FilterNode> GetFilters() =>
        [
            new FilterNode { Label = "所有项目", Kind = FilterKind.AllItems, Count = 1 },
            new FilterNode
            {
                Label = "组织",
                Kind = FilterKind.Organization,
                OrganizationId = "org-1",
                Count = 1,
            },
        ];

        public CipherEditorDraft GetDraft(string id) =>
            CipherEditorDraft.CreateDefault(VaultItemKind.Login);

        public Task SetFavoriteAsync(string id, bool favorite, CancellationToken ct = default)
        {
            Favorite = favorite;
            return Task.CompletedTask;
        }

        public Task MoveCiphersAsync(
            IReadOnlyCollection<string> ids,
            string? folderId,
            CancellationToken ct = default)
        {
            LastMovedIds = ids.ToArray();
            LastFolderId = folderId;
            return Task.CompletedTask;
        }

        public Task<string> SaveCipherAsync(
            CipherEditorDraft draft,
            string? editingId,
            CancellationToken ct = default) => Task.FromResult(ItemId);

        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RestoreCipherAsync(string id, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveFolderAsync(
            string? folderId,
            string name,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SyncAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteCiphersAsync(
            IReadOnlyCollection<string> ids,
            bool permanent,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task RestoreCiphersAsync(
            IReadOnlyCollection<string> ids,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
