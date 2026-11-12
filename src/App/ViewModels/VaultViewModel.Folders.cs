using App.Services;
using App.ViewModels.Models;

namespace App.ViewModels;

public partial class VaultViewModel
{
    private long _editorDraftVersion;

    public async Task<UiOperationResult> CreateFolderForEditorAsync(
        CipherEditorDraft expectedDraft,
        string name,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedDraft);
        if (ct.IsCancellationRequested || !IsEditing || !ReferenceEquals(EditorDraft, expectedDraft))
            return UiOperationResult.Cancelled();
        if (!CanEditEditorDraft)
            return UiOperationResult.Busy();
        if (string.IsNullOrWhiteSpace(name))
            return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB0223FA30FCFA275));

        var lifecycleEpoch = _lifecycleEpoch;
        var draftVersion = _editorDraftVersion;
        bool IsCurrent() => lifecycleEpoch == _lifecycleEpoch && draftVersion == _editorDraftVersion
            && IsEditing && ReferenceEquals(EditorDraft, expectedDraft);

        var result = await RunOperationAsync(
            VaultOperationKind.Folder,
            async token =>
            {
                if (token.IsCancellationRequested || !IsCurrent())
                    return UiOperationResult.Cancelled();

                string folderId;
                try
                {
                    folderId = await _service.CreateFolderAndGetIdAsync(name.Trim(), token);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException
                    && (token.IsCancellationRequested || !IsCurrent()))
                {
                    return UiOperationResult.Cancelled();
                }
                catch (Api.FolderCreateSuccessResponseReadException)
                {
                    DataStale = true;
                    OperationWarning = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB779DBE474B1E775);
                    OperationError = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0B7046E787B253BA);
                    return UiOperationResult.Failure(OperationError);
                }
                catch (Core.Services.VaultRefreshException)
                {
                    DataStale = true;
                    OperationWarning = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text03EBE3B65C4815D8);
                    ReloadFilters();
                    if (token.IsCancellationRequested || !IsCurrent())
                        return UiOperationResult.Cancelled();
                    FoldersChanged?.Invoke(this, EventArgs.Empty);
                    return UiOperationResult.Success();
                }
                catch (NotSupportedException)
                {
                    OperationError = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2FBDDDE710FFE11B);
                    return UiOperationResult.Failure(OperationError);
                }

                if (token.IsCancellationRequested || !IsCurrent())
                    return UiOperationResult.Cancelled();

                ReloadFilters();
                if (token.IsCancellationRequested || !IsCurrent())
                    return UiOperationResult.Cancelled();

                // Only the ID returned by this creation can update the captured draft.
                if (!string.IsNullOrWhiteSpace(folderId)
                    && FolderFilters.Any(folder => folder.FolderId == folderId))
                {
                    expectedDraft.FolderId = folderId;
                    OnPropertyChanged(nameof(EditorSelectedFolder));
                }
                else
                {
                    DataStale = true;
                    OperationWarning = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC1CBA76C737AFBE3);
                }
                FoldersChanged?.Invoke(this, EventArgs.Empty);
                return UiOperationResult.Success();
            },
            ct);

        if (result.IsSuccess && (ct.IsCancellationRequested || !IsCurrent()))
            return UiOperationResult.Cancelled();
        return result.IsSuccess ? result.Value : new UiOperationResult(result.Status, result.Error);
    }
}
