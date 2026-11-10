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
            return UiOperationResult.Failure("文件夹名称不能为空。");

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
                    OperationWarning = "文件夹可能已创建。请先同步确认，避免重复新建。";
                    OperationError = "无法读取新文件夹的服务器记录。";
                    return UiOperationResult.Failure(OperationError);
                }
                catch (Core.Services.VaultRefreshException)
                {
                    DataStale = true;
                    OperationWarning = "文件夹已创建，但本地刷新失败。请先同步，再从文件夹菜单选择。";
                    ReloadFilters();
                    if (token.IsCancellationRequested || !IsCurrent())
                        return UiOperationResult.Cancelled();
                    FoldersChanged?.Invoke(this, EventArgs.Empty);
                    return UiOperationResult.Success();
                }
                catch (NotSupportedException)
                {
                    OperationError = "当前服务不支持在编辑器中新建文件夹。";
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
                    OperationWarning = "文件夹已创建，但暂时无法在本地找到。请先同步，再从文件夹菜单选择。";
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
