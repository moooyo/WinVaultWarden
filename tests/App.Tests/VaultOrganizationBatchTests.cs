using System.Xml.Linq;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class VaultOrganizationBatchTests
{
    [Fact]
    public async Task PersonalSelection_TransfersAllBaselinesAndClearsSelection()
    {
        var service = new BatchVaultUiService(
            Item("first", null, canTransfer: true),
            Item("second", null, canTransfer: true));
        var viewModel = SelectAll(service);

        Assert.True(viewModel.CanTransferSelectionToOrganization);
        Assert.False(viewModel.CanManageSelectionCollections);

        var result = await viewModel.TransferSelectedCiphersToOrganizationAsync(
            "org-1",
            ["collection-1"]);

        Assert.True(result.IsSuccess);
        Assert.False(viewModel.IsSelectionMode);
        Assert.Equal(["first", "second"], service.LastTransfer!.Value.Baselines.Select(x => x.CipherId));
        Assert.Equal("org-1", service.LastTransfer.Value.OrganizationId);
        Assert.Equal(["collection-1"], service.LastTransfer.Value.CollectionIds);
    }

    [Fact]
    public async Task SameOrganizationSelection_CanAddAndRemoveCollections()
    {
        var service = new BatchVaultUiService(
            Item("first", "org-1"),
            Item("second", "org-1"));
        var viewModel = SelectAll(service);

        Assert.False(viewModel.CanTransferSelectionToOrganization);
        Assert.True(viewModel.CanManageSelectionCollections);
        Assert.Equal("org-1", viewModel.SelectedOrganizationId);

        var result = await viewModel.RemoveSelectedCiphersFromCollectionsAsync(
            ["collection-2"]);

        Assert.True(result.IsSuccess);
        Assert.True(service.LastCollectionUpdate!.Value.RemoveCollections);
        Assert.Equal(["first", "second"], service.LastCollectionUpdate.Value.Baselines.Select(x => x.CipherId));
        Assert.Equal(["collection-2"], service.LastCollectionUpdate.Value.CollectionIds);
    }

    [Fact]
    public void MixedOrCrossOrganizationSelection_IsDisabledWithExplicitError()
    {
        var mixed = SelectAll(new BatchVaultUiService(
            Item("personal", null, canTransfer: true),
            Item("shared", "org-1")));

        Assert.False(mixed.CanTransferSelectionToOrganization);
        Assert.False(mixed.CanManageSelectionCollections);
        Assert.True(mixed.HasSelectionPlacementError);
        Assert.Contains("全部个人条目或同一组织", mixed.SelectionPlacementError);

        var crossOrganization = SelectAll(new BatchVaultUiService(
            Item("first", "org-1"),
            Item("second", "org-2")));

        Assert.False(crossOrganization.CanManageSelectionCollections);
        Assert.Contains("来自不同组织", crossOrganization.SelectionPlacementError);
    }

    [Fact]
    public void PermissionAndTypeCapability_AreRequiredForBulkOrganizationActions()
    {
        var hiddenPassword = SelectAll(new BatchVaultUiService(
            Item("first", "org-1", canViewPassword: false, canManageCollections: false)));
        Assert.False(hiddenPassword.CanManageSelectionCollections);
        Assert.Contains("无权管理集合", hiddenPassword.SelectionPlacementError);

        var collectionManager = SelectAll(new BatchVaultUiService(
            Item(
                "managed",
                "org-1",
                canViewPassword: false,
                canEdit: false,
                canManageCollections: true)));
        Assert.True(collectionManager.CanManageSelectionCollections);

        var unsupportedType = SelectAll(new BatchVaultUiService(
            Item("bank", "org-1", kind: VaultItemKind.BankAccount)));
        Assert.False(unsupportedType.CanManageSelectionCollections);
        Assert.Contains("不支持写入的类型", unsupportedType.SelectionPlacementError);
    }

    [Fact]
    public async Task PartialCollectionCommit_ShowsWarningAndKeepsAuthoritativeSelection()
    {
        var service = new BatchVaultUiService(
            Item("first", "org-1"),
            Item("second", "org-1"))
        {
            Failure = new VaultMutationReconciliationException(
                "partial",
                ["second"]),
        };
        var viewModel = SelectAll(service);
        var selectionRebindRequested = false;
        viewModel.SelectionRebindRequested += (_, _) => selectionRebindRequested = true;

        var result = await viewModel.AddSelectedCiphersToCollectionsAsync(
            ["collection-2"]);

        Assert.False(result.IsSuccess);
        Assert.True(viewModel.IsSelectionMode);
        Assert.Equal(2, viewModel.SelectedCount);
        Assert.True(selectionRebindRequested);
        Assert.Contains("部分完成", viewModel.OperationWarning);
        Assert.Contains("未对所有", viewModel.OperationError);
    }

    [Fact]
    public async Task OrganizationClone_PreservesOnlyWritableCollectionSelections()
    {
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        draft.OrganizationId = "org-1";
        draft.Name = "Shared";
        draft.ReplaceCollectionIds(["collection-1", "readonly"]);
        var service = new BatchVaultUiService(
            Item("shared", "org-1", canManageCollections: true, canClone: true))
        {
            Draft = draft,
        };
        var viewModel = new VaultViewModel(service);

        var result = await viewModel.BeginCloneAsync(
            "shared",
            _ => Task.FromResult<string?>(null));

        Assert.True(result.IsSuccess);
        Assert.Equal(["collection-1"], viewModel.EditorDraft!.CollectionIds);
        var option = Assert.Single(viewModel.EditorCollectionOptions);
        Assert.Equal("collection-1", option.Id);
        Assert.True(option.IsSelected);
    }

    [Fact]
    public void VaultPage_ExposesWiredBatchOrganizationActionsAndSelectionError()
    {
        var document = XDocument.Load(RepoFile("src", "App", "Views", "VaultPage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Equal(
            "OnTransferSelectedToOrganizationClick",
            Named(document, x, "TransferSelectedToOrganizationButton").Attribute("Click")?.Value);
        Assert.Equal(
            "OnAddSelectedToCollectionsClick",
            Named(document, x, "AddSelectedToCollectionsButton").Attribute("Click")?.Value);
        Assert.Equal(
            "OnRemoveSelectedFromCollectionsClick",
            Named(document, x, "RemoveSelectedFromCollectionsButton").Attribute("Click")?.Value);
        Assert.Contains(
            "ViewModel.SelectionPlacementError",
            Named(document, x, "VaultSelectionPlacementErrorInfoBar").Attribute("Message")?.Value);

        var codeBehind = File.ReadAllText(RepoFile("src", "App", "Views", "VaultPage.xaml.cs"));
        Assert.Contains("ViewModel.IsRebuildingItems", codeBehind);
        Assert.Contains("SelectionRebindRequested", codeBehind);
    }

    private static VaultViewModel SelectAll(BatchVaultUiService service)
    {
        var viewModel = new VaultViewModel(service);
        viewModel.ToggleSelectionModeCommand.Execute(null);
        foreach (var item in service.GetItems())
            viewModel.ToggleSelection(item.Id);
        return viewModel;
    }

    private static CipherListItem Item(
        string id,
        string? organizationId,
        bool canTransfer = false,
        bool canViewPassword = true,
        bool canEdit = true,
        bool? canManageCollections = null,
        bool canClone = false,
        VaultItemKind kind = VaultItemKind.Login) => new()
    {
        Id = id,
        Name = id,
        Kind = kind,
        OrganizationId = organizationId,
        CollectionIds = organizationId is null ? [] : ["collection-1"],
        CanEdit = canEdit,
        CanViewPassword = canViewPassword,
        CanClone = canClone,
        CanTransferToOrganization = canTransfer,
        CanManageCollections = canManageCollections ?? organizationId is not null,
        RevisionDate = DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
    };

    private static XElement Named(XDocument document, XNamespace x, string name) =>
        document.Descendants().Single(element => element.Attribute(x + "Name")?.Value == name);

    private static string RepoFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }

    private sealed class BatchVaultUiService(params CipherListItem[] items) : IVaultUiService
    {
        private readonly IReadOnlyList<CipherListItem> _items = items;

        public Exception? Failure { get; init; }
        public CipherEditorDraft? Draft { get; init; }
        public (IReadOnlyCollection<CipherPlacementBaseline> Baselines, string OrganizationId, IReadOnlyCollection<string> CollectionIds)? LastTransfer { get; private set; }
        public (IReadOnlyCollection<CipherPlacementBaseline> Baselines, string OrganizationId, IReadOnlyCollection<string> CollectionIds, bool RemoveCollections)? LastCollectionUpdate { get; private set; }
        public int SkippedCipherCount => 0;
        public bool DataStale => false;
        public VaultCapabilities Capabilities => VaultCapabilities.None;

        public IReadOnlyList<CipherListItem> GetItems() => _items;
        public CipherAccessPolicy GetAccessPolicy(string id) => new(id, false);
        public CipherDetail GetDetail(string id, bool includeSensitive) => new LoginDetail
        {
            Id = id,
            Name = id,
            SensitiveUnlocked = true,
        };
        public IReadOnlyList<FilterNode> GetFilters() =>
        [
            new FilterNode { Label = "All", Kind = FilterKind.AllItems, Count = _items.Count },
            new FilterNode { Label = "Team", Kind = FilterKind.Organization, OrganizationId = "org-1" },
        ];
        public CipherEditorDraft GetDraft(string id) =>
            Draft ?? CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        public IReadOnlyList<CipherDestinationOption> GetCipherDestinations() =>
        [
            CipherDestinationOption.Personal,
            new CipherDestinationOption("Team", "org-1", null),
        ];
        public IReadOnlyList<CipherCollectionOption> GetCipherCollectionOptions(
            string organizationId,
            IReadOnlyCollection<string>? selectedCollectionIds = null,
            bool lockReadOnlyAssignments = false)
        {
            var selected = (selectedCollectionIds ?? []).ToHashSet(StringComparer.Ordinal);
            var options = new List<CipherCollectionOption>
            {
                new()
                {
                    Id = "collection-1",
                    OrganizationId = organizationId,
                    Name = "One",
                    IsSelected = selected.Contains("collection-1"),
                },
            };
            if (selected.Contains("readonly"))
            {
                options.Add(new CipherCollectionOption
                {
                    Id = "readonly",
                    OrganizationId = organizationId,
                    Name = "Read only",
                    IsSelected = true,
                    IsLocked = lockReadOnlyAssignments,
                });
            }
            return options;
        }
        public CipherPlacementBaseline GetCipherPlacementBaseline(string id)
        {
            var item = _items.Single(candidate => candidate.Id == id);
            return new CipherPlacementBaseline(
                id,
                item.Kind == VaultItemKind.BankAccount ? CipherType.BankAccount : CipherType.Login,
                item.RevisionDate,
                item.OrganizationId,
                item.CollectionIds);
        }

        public Task TransferCiphersToOrganizationAsync(
            IReadOnlyCollection<CipherPlacementBaseline> baselines,
            string organizationId,
            IReadOnlyCollection<string> collectionIds,
            CancellationToken ct = default)
        {
            LastTransfer = (baselines, organizationId, collectionIds);
            return Complete();
        }

        public Task BulkUpdateCipherCollectionsAsync(
            IReadOnlyCollection<CipherPlacementBaseline> baselines,
            string organizationId,
            IReadOnlyCollection<string> collectionIds,
            bool removeCollections,
            CancellationToken ct = default)
        {
            LastCollectionUpdate = (baselines, organizationId, collectionIds, removeCollections);
            return Complete();
        }

        private Task Complete() => Failure is null
            ? Task.CompletedTask
            : Task.FromException(Failure);

        public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default) =>
            Task.FromResult(editingId ?? "new");
        public Task DeleteCipherAsync(string id, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreCipherAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SyncAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Task.CompletedTask;
    }
}
