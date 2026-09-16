using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

// 条目级门禁的"授权前不取秘密"覆盖:编辑/复制/TOTP/附件在拒绝或缺失门禁时,底层敏感读取/服务调用数必为 0;
// 脱敏投影不含任何秘密;PasskeyApprovalDialogService 在收集 evidence 前先过条目 gate(源码守卫)。
public sealed class VaultRepromptCoverageTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ClipboardFailureIsReportedWithoutRevealingTheProtectedDetail(bool passwordOnly, bool externalFailure)
    {
        var clipboard = new FailingClipboard(externalFailure);
        var service = new RecordingVaultUiService(reprompt: true);
        var vm = new VaultViewModel(service, clipboard, reprompt: new GrantingItemGate());
        vm.SelectedItem = vm.Items.Single();
        var originalDetail = vm.Detail;

        var result = passwordOnly
            ? await vm.CopyPasswordAsync("cipher-a", _ => Task.FromResult<string?>("master"))
            : await vm.CopyPrimaryAsync("cipher-a", _ => Task.FromResult<string?>("master"));

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.True(vm.HasOperationError);
        Assert.Equal(vm.OperationError, result.Error);
        Assert.DoesNotContain(FailingClipboard.UnsafeMessage, vm.OperationError);
        Assert.Same(originalDetail, vm.Detail);
        Assert.Null(Assert.IsType<LoginDetail>(vm.Detail).Password);
        Assert.False(vm.Detail!.SensitiveUnlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldCopyReportsRecoverableClipboardFailure(bool externalFailure)
    {
        var vm = new VaultViewModel(new RecordingVaultUiService(reprompt: false), new FailingClipboard(externalFailure));

        vm.CopyCommand.Execute("private-field-content");

        Assert.True(vm.HasOperationError);
        Assert.DoesNotContain("private-field-content", vm.OperationError);
        Assert.DoesNotContain(FailingClipboard.UnsafeMessage, vm.OperationError);
    }

    [Fact]
    public async Task CopyProtectedPasswordDoesNotRevealOrReprojectTheDetail()
    {
        var clipboard = new RecordingClipboard();
        var service = new RecordingVaultUiService(reprompt: true);
        var vm = new VaultViewModel(service, clipboard, reprompt: new GrantingItemGate());
        vm.SelectedItem = vm.Items.Single();
        var originalDetail = vm.Detail;

        var result = await vm.CopyPasswordAsync("cipher-a", _ => Task.FromResult<string?>("master"));

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(clipboard.Text!);
        Assert.Same(originalDetail, vm.Detail);
        Assert.Null(Assert.IsType<LoginDetail>(vm.Detail).Password);
        Assert.False(vm.Detail!.SensitiveUnlocked);
    }

    [Fact]
    public async Task DeniedPasswordCopyDoesNotReadOrCopyASensitiveValue()
    {
        var clipboard = new RecordingClipboard();
        var service = new RecordingVaultUiService(reprompt: true);
        var vm = new VaultViewModel(service, clipboard, reprompt: new DenyingItemGate());

        var result = await vm.CopyPasswordAsync("cipher-a", _ => Task.FromResult<string?>("wrong"));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, service.SensitiveDetailReads);
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public async Task DeniedEdit_DoesNotReadDraft()
    {
        var service = new RecordingVaultUiService(reprompt: true);
        var vm = new VaultViewModel(service, reprompt: new DenyingItemGate());

        var result = await vm.BeginEditAsync(
            "cipher-a",
            _ => Task.FromResult<string?>("wrong"));

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(0, service.GetDraftCalls);
    }

    [Fact]
    public async Task DeniedClone_DoesNotReadDraft()
    {
        var service = new RecordingVaultUiService(reprompt: true);
        var vm = new VaultViewModel(service, reprompt: new DenyingItemGate());

        var result = await vm.BeginCloneAsync(
            "cipher-a",
            _ => Task.FromResult<string?>("wrong"));

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(0, service.GetDraftCalls);
    }

    [Fact]
    public async Task HiddenPasswordOrganizationItemRejectsSensitiveActionsBeforeReadingSecrets()
    {
        var clipboard = new RecordingClipboard();
        var service = new RecordingVaultUiService(reprompt: true, canViewPassword: false);
        var vm = new VaultViewModel(service, clipboard, reprompt: new GrantingItemGate());
        vm.SelectedItem = vm.Items.Single();

        var edit = await vm.BeginEditAsync("cipher-a", _ => Task.FromResult<string?>("master"));
        var clone = await vm.BeginCloneAsync("cipher-a", _ => Task.FromResult<string?>("master"));
        var copy = await vm.CopyPrimaryAsync("cipher-a", _ => Task.FromResult<string?>("master"));
        var unlock = await vm.UnlockDetailAsync("cipher-a", _ => Task.FromResult<string?>("master"));

        Assert.All(new[] { edit, clone, copy }, result => Assert.Equal(UiOperationStatus.Failed, result.Status));
        Assert.Equal(UiOperationStatus.Failed, unlock.Status);
        Assert.Contains("隐藏密码", edit.Error);
        Assert.Contains("隐藏密码", clone.Error);
        Assert.Contains("隐藏密码", copy.Error);
        Assert.Contains("隐藏密码", unlock.Error);
        Assert.Equal(0, service.GetDraftCalls);
        Assert.Equal(0, service.SensitiveDetailReads);
        Assert.Null(clipboard.Text);
        Assert.False(vm.IsEditing);
        Assert.False(vm.ShowSensitiveUnlock);
    }

    [Fact]
    public async Task PermissionRevokedDuringAuthorizationReturnsFailureInsteadOfExposingDraft()
    {
        var service = new RecordingVaultUiService(
            reprompt: true,
            canViewPassword: true,
            rejectDraftRead: true);
        var vm = new VaultViewModel(service, reprompt: new GrantingItemGate());

        var edit = await vm.BeginEditAsync("cipher-a", _ => Task.FromResult<string?>("master"));
        var clone = await vm.BeginCloneAsync("cipher-a", _ => Task.FromResult<string?>("master"));

        Assert.Equal(UiOperationStatus.Failed, edit.Status);
        Assert.Equal(UiOperationStatus.Failed, clone.Status);
        Assert.Contains("权限已被撤销", edit.Error);
        Assert.Contains("权限已被撤销", clone.Error);
        Assert.Equal(2, service.GetDraftCalls);
        Assert.Null(vm.EditorDraft);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void LockedProjection_DoesNotContainTotpOrOtherSecrets()
    {
        var service = new RecordingVaultUiService(reprompt: true);

        var detail = Assert.IsType<LoginDetail>(
            service.GetDetail("cipher-a", includeSensitive: false));

        Assert.Null(detail.Password);
        Assert.Null(detail.TotpSecret);
        Assert.Empty(detail.PasswordHistory);
        Assert.DoesNotContain(detail.CustomFields, field => field.IsSecret && field.Value.Length > 0);
    }

    [Fact]
    public async Task MissingGate_FailsClosedForRepromptCopy()
    {
        var clipboard = new RecordingClipboard();
        var service = new RecordingVaultUiService(reprompt: true);
        var vm = new VaultViewModel(service, clipboard, reprompt: null);

        var result = await vm.CopyPrimaryAsync(
            "cipher-a",
            _ => Task.FromResult<string?>("master"));

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Null(clipboard.Text);
        Assert.Equal(0, service.SensitiveDetailReads);
    }

    [Fact]
    public async Task DeniedAttachment_DoesNotCallAttachmentService()
    {
        var attachments = new RecordingAttachments();
        var vm = new VaultViewModel(
            new RecordingVaultUiService(reprompt: true),
            attachments: attachments,
            reprompt: new DenyingItemGate());

        var result = await vm.DownloadAttachmentAsync(
            "cipher-a",
            "attachment-a",
            _ => Task.FromResult<string?>("wrong"),
            new MemoryStream());

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        Assert.Equal(0, attachments.DownloadCalls);
    }

    [Fact]
    public async Task DeniedTotpUnlock_KeepsTotpSecretNull_AndReadsNoSensitiveDetail()
    {
        var service = new RecordingVaultUiService(reprompt: true);
        var vm = new VaultViewModel(service, reprompt: new DenyingItemGate());
        vm.SelectedItem = vm.Items.First(i => i.Id == "cipher-a");

        var locked = Assert.IsType<LoginDetail>(vm.Detail);
        Assert.Null(locked.TotpSecret);

        var result = await vm.UnlockDetailAsync(
            "cipher-a",
            _ => Task.FromResult<string?>("wrong"));

        Assert.Equal(UiOperationStatus.Failed, result.Status);
        var stillLocked = Assert.IsType<LoginDetail>(vm.Detail);
        Assert.Null(stillLocked.TotpSecret);
        Assert.Equal(0, service.SensitiveDetailReads);
    }

    [Fact]
    public async Task AuthorizedUnlock_ExposesTotpSecret_AfterVerification()
    {
        var service = new RecordingVaultUiService(reprompt: true);
        var vm = new VaultViewModel(service, reprompt: new GrantingItemGate());
        vm.SelectedItem = vm.Items.First(i => i.Id == "cipher-a");

        var locked = Assert.IsType<LoginDetail>(vm.Detail);
        Assert.Null(locked.TotpSecret);

        var result = await vm.UnlockDetailAsync(
            "cipher-a",
            _ => Task.FromResult<string?>("master"));

        Assert.Equal(UiOperationStatus.Succeeded, result.Status);
        var unlocked = Assert.IsType<LoginDetail>(vm.Detail);
        Assert.Equal("JBSWY3DPEHPK3PXP", unlocked.TotpSecret);
        Assert.True(unlocked.SensitiveUnlocked);
        Assert.Equal(1, service.SensitiveDetailReads);
    }

    [Fact]
    public void PasskeyApproval_GatesCipherBeforeReturningApprovalEvidence()
    {
        var source = File.ReadAllText(FindAppFile(
            "Services", "PasskeyApprovalDialogService.cs"));
        var gate = source.IndexOf(
            "SensitiveItemAction.UsePasskey",
            StringComparison.Ordinal);
        var evidence = source.IndexOf(
            "new PasskeyApprovalEvidence",
            StringComparison.Ordinal);
        Assert.True(gate >= 0 && gate < evidence);
    }

    private static string FindAppFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                [directory.FullName, "src", "App", .. parts]);
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException(string.Join(
            Path.DirectorySeparatorChar,
            parts));
    }

    private sealed class DenyingItemGate : IItemRepromptGate
    {
        public bool IsAuthorized(CipherAccessPolicy policy) => false;
        public Task<UiOperationResult<bool>> AuthorizeAsync(
            CipherAccessPolicy policy,
            SensitiveItemAction action,
            Func<CancellationToken, Task<string?>> prompt,
            CancellationToken ct = default) =>
            Task.FromResult(UiOperationResult<bool>.Failure("denied"));
        public void ClearSensitive() { }
    }

    // 初始未授权(IsAuthorized=false),AuthorizeAsync 成功后翻转为已授权——镜像真实 gate 的"验证一次即缓存"。
    private sealed class GrantingItemGate : IItemRepromptGate
    {
        private bool _granted;
        public bool IsAuthorized(CipherAccessPolicy policy) => !policy.RequiresReprompt || _granted;
        public Task<UiOperationResult<bool>> AuthorizeAsync(
            CipherAccessPolicy policy,
            SensitiveItemAction action,
            Func<CancellationToken, Task<string?>> prompt,
            CancellationToken ct = default)
        {
            _granted = true;
            return Task.FromResult(UiOperationResult<bool>.Success(true));
        }
        public void ClearSensitive() => _granted = false;
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
        public void SetSecretText(string text, int? autoClearSeconds = null) =>
            Text = text;
    }

    private sealed class FailingClipboard(bool externalFailure) : IClipboardService
    {
        public const string UnsafeMessage = "private-value-from-provider";

        public void SetText(string text)
        {
            if (externalFailure)
                throw new System.Runtime.InteropServices.ExternalException(UnsafeMessage);
            throw new InvalidOperationException(UnsafeMessage);
        }

        public void SetSecretText(string text, int? autoClearSeconds = null) => SetText(text);
    }

    private sealed class RecordingAttachments : IAttachmentUiService
    {
        public int DownloadCalls { get; private set; }
        public Task<IReadOnlyList<AttachmentItem>> AddAttachmentAsync(
            string cipherId,
            Core.Models.ReopenableFileSource file,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AttachmentItem>>([]);
        public Task<long> DownloadAttachmentAsync(
            string cipherId,
            string attachmentId,
            Stream plaintextDestination,
            CancellationToken ct = default)
        {
            DownloadCalls++;
            return Task.FromResult(0L);
        }
        public Task<IReadOnlyList<AttachmentItem>> DeleteAttachmentAsync(string cipherId, string attachmentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AttachmentItem>>([]);
    }

    private sealed class RecordingVaultUiService(
        bool reprompt,
        bool canViewPassword = true,
        bool rejectDraftRead = false)
        : IVaultUiService
    {
        public int GetDraftCalls { get; private set; }
        public int SensitiveDetailReads { get; private set; }
        public int SkippedCipherCount => 0;
        public bool DataStale => false;
        public IReadOnlyList<CipherListItem> GetItems() =>
        [
            new CipherListItem
            {
                Id = "cipher-a",
                Name = "A",
                Kind = VaultItemKind.Login,
                CanViewPassword = canViewPassword,
                CanClone = true,
            },
        ];
        public CipherAccessPolicy GetAccessPolicy(string id) =>
            new(id, reprompt);
        public CipherDetail GetDetail(string id, bool includeSensitive)
        {
            if (includeSensitive)
                SensitiveDetailReads++;
            return new LoginDetail
            {
                Id = id,
                Name = "A",
                Reprompt = reprompt,
                SensitiveUnlocked = includeSensitive,
                Password = includeSensitive ? "secret" : null,
                TotpSecret = includeSensitive ? "JBSWY3DPEHPK3PXP" : null,
            };
        }
        public IReadOnlyList<FilterNode> GetFilters() =>
        [
            new FilterNode { Label = "所有项目", Kind = FilterKind.AllItems },
        ];
        public CipherEditorDraft GetDraft(string id)
        {
            GetDraftCalls++;
            if (rejectDraftRead)
            {
                throw new Core.Services.OrganizationCipherOperationException(
                    "Password viewing permission was revoked.");
            }
            return CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        }
        public Task<string> SaveCipherAsync(CipherEditorDraft draft, string? editingId, CancellationToken ct = default) => Task.FromResult(editingId ?? "new");
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
