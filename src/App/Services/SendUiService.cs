using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using Vault;
using AppSendType = App.ViewModels.Models.SendType;
using CoreSendType = Core.Enums.SendType;

namespace App.Services;

// 真实 Send UI 服务:把 App 层 SendEditorDraft/SendListItem(SendType Text=0,File=1)
// 映射到 Core 领域(SendDraftModel + Core.Enums.SendType Text=0,File=1),并委派给
// ISendService / ISendWriteService / ISendAccessService。删除日期相对标签在此转绝对时间(<=31 天)。
//
// 演示/离线会话(VaultSession.IsDemo)特判:该会话未配置服务端地址,任何走网络的 Send 读写都会抛
// 「An invalid request URI was provided…」。故 IsDemo 时全部改走会话内存中的离线 Send 列表(读/建/改/删),
// 完全不触网,并展示演示数据。session 为 null(设计时/旧构造)时按在线路径处理。
public sealed class SendUiService : ISendUiService
{
    private readonly ISendService _read;
    private readonly ISendWriteService _write;
    private readonly ISendAccessService _access;
    private readonly VaultSession? _session;

    public SendUiService(ISendService read, ISendWriteService write, ISendAccessService access, VaultSession? session = null)
    {
        _read = read;
        _write = write;
        _access = access;
        _session = session;
    }

    private bool IsDemo => _session?.IsDemo == true;

    public async Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default)
    {
        if (IsDemo)
            return _session!.Sends.Select(ToListItem).ToList();

        var sends = await _read.GetSendsAsync(ct);
        return sends.Select(ToListItem).ToList();
    }

    public async Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default)
    {
        var sends = IsDemo ? _session!.Sends : await _read.GetSendsAsync(ct);
        var send = sends.FirstOrDefault(s => s.Id == id);
        return send is null ? null : SendEditorDraft.FromExisting(send);
    }

    public async Task<SendListItem> CreateSendAsync(SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            var created = BuildDemoSend(draft, fileBytes, id: Guid.NewGuid().ToString("N"), existing: null);
            _session!.UpsertSend(created);
            return ToListItem(created);
        }

        var model = ToDraftModel(draft, id: null);
        await WriteSendAsync(model, id: null, fileBytes, ct);
        return await ReloadItemAsync(draft, ct);
    }

    public async Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            var existing = _session!.Sends.FirstOrDefault(s => s.Id == id);
            var updated = BuildDemoSend(draft, fileBytes, id, existing);
            _session!.UpsertSend(updated);
            return ToListItem(updated);
        }

        var model = ToDraftModel(draft, id);
        await WriteSendAsync(model, id, fileBytes, ct);
        return await ReloadItemAsync(draft, ct, id);
    }

    // 文件型 Send 的单一流契约边界:创建(id==null)与替换(id!=null 且重选了字节)都把 UI 选到的字节
    // 包装成可重开内存源交给写入层的【流式】实现;仅改元数据(id!=null 且未重选文件)只走 PUT。
    // 绝不把 null 当零字节,也不保留任何 byte[] 上传重载。
    private async Task WriteSendAsync(SendDraftModel model, string? id, byte[]? fileBytes, CancellationToken ct)
    {
        if (model.Type != CoreSendType.File)
        {
            await _write.SaveTextSendAsync(model, ct);
            return;
        }

        if (id is null)
            await _write.CreateFileSendAsync(model, RequireMemorySource(model.FileName!, fileBytes), ct);
        else if (fileBytes is null)
            await _write.UpdateFileSendMetadataAsync(model, ct);
        else
            await _write.ReplaceFileSendAsync(id, model, RequireMemorySource(model.FileName!, fileBytes), ct);

        static SendFileSource RequireMemorySource(string fileName, byte[]? bytes)
        {
            if (bytes is null)
                throw new InvalidOperationException("创建文件 Send 必须选择文件。");
            SendFileSource.ValidateLength(bytes.LongLength);
            return new SendFileSource(
                fileName,
                bytes.LongLength,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)));
        }
    }

    public Task DeleteSendAsync(string id, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            _session!.RemoveSend(id);
            return Task.CompletedTask;
        }

        return _write.DeleteSendAsync(id, ct);
    }

    public string? CopyShareLink(SendListItem item) => item.Link;

    public async Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default)
    {
        // 演示/离线会话无法访问外部 Send 服务端:明确提示而非抛出相对 URI 异常。
        if (IsDemo)
            return SendReceivedResult.Failure("演示保险库为本地离线数据，无法打开外部 Send 链接。");

        SendAccessResult accessed;
        try
        {
            accessed = await _access.AccessAsync(url, password, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return SendReceivedResult.Wrong();
        }
        catch (Exception ex)
        {
            return SendReceivedResult.Failure(ex.Message);
        }

        return new SendReceivedResult(
            Ok: true,
            WrongPassword: false,
            Type: MapType(accessed.Type),
            Name: accessed.Name,
            TextContent: accessed.TextContent,
            FileName: accessed.FileName,
            Error: null,
            Accessed: accessed);
    }

    private async Task<SendListItem> ReloadItemAsync(SendEditorDraft draft, CancellationToken ct, string? preferId = null)
    {
        // 写后重新拉取以拿到服务端 accessId/share URL;按名称/id 命中刚写入的项。
        var items = await GetSendsAsync(ct);
        var name = draft.Type == AppSendType.File && !string.IsNullOrWhiteSpace(draft.FileName) ? draft.FileName : draft.Name;
        return (preferId is not null ? items.FirstOrDefault(i => i.Id == preferId) : null)
            ?? items.LastOrDefault(i => i.Name == name)
            ?? items.LastOrDefault()
            ?? new SendListItem(preferId ?? "", name, draft.Type, $"{draft.DeletionDateLabel} 后删除", null);
    }

    private SendListItem ToListItem(Send send)
    {
        var deletion = send.DeletionDate.LocalDateTime.ToString("yyyy/M/d HH:mm");
        return new SendListItem(send.Id, send.Name, MapType(send.Type), $"{deletion} 截止", send.ShareUrl);
    }

    private SendDraftModel ToDraftModel(SendEditorDraft draft, string? id) => new()
    {
        Id = id,
        Key = draft.WrappedKey,
        Type = MapType(draft.Type),
        Name = draft.Name.Trim(),
        Notes = NullIfBlank(draft.Notes),
        TextContent = draft.Type == AppSendType.Text ? draft.Text : null,
        TextHidden = draft.HideTextByDefault,
        FileName = draft.Type == AppSendType.File ? NullIfBlank(draft.FileName) : null,
        MaxAccessCount = draft.MaxAccessCount,
        ExpirationDate = draft.ExpirationDate,
        DeletionDate = draft.ToDeletionDate(),
        Disabled = draft.Disabled,
        HideEmail = draft.HideEmail,
        PasswordChange = string.IsNullOrWhiteSpace(draft.Password)
            ? new SendPasswordChange.Keep()
            : new SendPasswordChange.Set(draft.Password),
    };

    private static AppSendType MapType(CoreSendType type) => type == CoreSendType.File ? AppSendType.File : AppSendType.Text;
    private static CoreSendType MapType(AppSendType type) => type == AppSendType.File ? CoreSendType.File : CoreSendType.Text;
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // 演示/离线会话:直接把可编辑草稿映射成内存 Send 领域模型(不加密、不上传)。ShareUrl 为占位链接,
    // 仅供「复制链接」演示。文件 Send 若未重新选取文件(fileBytes==null)则复用既有 File 元数据
    // (Size/SizeName/FileId/FileName),避免 FromExisting 不带文件字节导致的大小归零;
    // HasPassword 同理在草稿未填新密码时沿用既有值(FromExisting 不回填明文密码)。
    private static Send BuildDemoSend(SendEditorDraft draft, byte[]? fileBytes, string id, Send? existing)
    {
        var type = MapType(draft.Type);
        SendFile? file = null;
        if (type == CoreSendType.File)
        {
            if (fileBytes is null && existing?.File is not null)
            {
                file = existing.File;
            }
            else
            {
                long fileSize = fileBytes?.Length ?? 0;
                file = new SendFile(NullIfBlank(draft.FileName) ?? draft.Name.Trim(), fileSize, HumanSize(fileSize), id);
            }
        }

        return new Send
        {
            Id = id,
            AccessId = id,
            Type = type,
            Name = draft.Name.Trim(),
            Notes = NullIfBlank(draft.Notes),
            Text = type == CoreSendType.Text ? new SendText(draft.Text, draft.HideTextByDefault) : null,
            File = file,
            MaxAccessCount = draft.MaxAccessCount,
            ExpirationDate = draft.ExpirationDate,
            DeletionDate = draft.ToDeletionDate(),
            Disabled = draft.Disabled,
            HideEmail = draft.HideEmail,
            HasPassword = !string.IsNullOrWhiteSpace(draft.Password) || (existing?.HasPassword ?? false),
            ShareUrl = $"https://demo.local/#/send/{id}/demoAccessKey",
        };
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
