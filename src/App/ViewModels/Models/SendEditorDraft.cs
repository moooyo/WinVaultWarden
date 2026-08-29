using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels.Models;

public sealed partial class SendEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial SendType Type { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeletionDateLabel { get; set; } = "7 天";

    [ObservableProperty]
    public partial string ViewerLabel { get; set; } = "拥有链接的任何人";

    [ObservableProperty]
    public partial int? MaxAccessCount { get; set; }

    [ObservableProperty]
    public partial bool HideTextByDefault { get; set; }

    [ObservableProperty]
    public partial bool HideEmail { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Disabled { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    private SendExpirationSelection _expirationSelection =
        SendExpirationSelection.FromExpiration(expirationDate: null);

    public DateTimeOffset? ExpirationDate
    {
        get => _expirationSelection.ExpirationDate;
        set => SetExpirationSelection(SendExpirationSelection.FromExpiration(value));
    }

    public string ExpirationDateLabel => _expirationSelection.Label;

    public bool IsCustomExpiration =>
        ExpirationDateLabel == SendExpirationSelection.CustomLabel;

    public DateTimeOffset? CustomExpirationDate =>
        IsCustomExpiration && ExpirationDate is { } expiration
            ? expiration.ToLocalTime()
            : null;

    public string CustomExpirationDescription =>
        CustomExpirationDate is { } expiration
            ? $"{expiration:yyyy/MM/dd HH:mm:ss zzz}"
            : string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? DeletionDate { get; set; }

    public bool IsText => Type == SendType.Text;
    public bool IsFile => Type == SendType.File;

    // 选中要替换/新建的可重开文件源(前层第二层最终契约)。非绑定属性,故用普通自动属性,避免进入 x:Bind。
    // App 层绝不缓存整份明文字节:选择文件时只记录名称/长度,加密时由前层按两遍式契约按需重开流。
    public Core.Models.SendFileSource? ReplacementFile { get; set; }

    public long? ReplacementFileLength => ReplacementFile?.Length;

    // 服务端 wrapped seed(akey)。非绑定属性(同 ReplacementFile,不进 x:Bind)。编辑时用于让写入层复用原 seed。
    public string? WrappedKey { get; set; }

    // A protected file replacement must collect plaintext password input because the server-side
    // verifier is not a reusable create proof. This flag is state only and is not bound to the UI.
    public bool ExistingHasPassword { get; set; }
    public Core.Enums.SendAuthType ExistingAuthType { get; set; } = Core.Enums.SendAuthType.Unknown;
    public int ExistingRawAuthType { get; set; } = int.MinValue;
    public Core.Session.VaultSessionLease? OriginatingSession { get; set; }

    // 锁屏/登出或对话框强制关闭时清空内存中的敏感字段:文本/密码/备注,以及选中的文件源与 wrapped key。
    public void ClearSensitive()
    {
        Text = string.Empty;
        Password = string.Empty;
        Notes = string.Empty;
        ReplacementFile = null;
        WrappedKey = null;
        OriginatingSession = null;
    }

    // 相对删除标签 → 绝对删除时间(服务端要求 <=31 天)。"自定义" 用 DeletionDate(为空则回退 7 天)。
    public DateTimeOffset ToDeletionDate()
    {
        var now = DateTimeOffset.UtcNow;
        var days = DeletionDateLabel switch
        {
            "1 天" => 1,
            "7 天" => 7,
            "30 天" => 30,
            _ => -1,
        };
        if (days >= 0)
            return now.AddDays(days);

        var picked = DeletionDate ?? now.AddDays(7);
        var max = now.AddDays(31);
        return picked > max ? max : picked;
    }

    public static SendEditorDraft CreateDefault(SendType type) => new() { Type = type };

    public void SelectExpiration(string label, DateTimeOffset now) =>
        SetExpirationSelection(_expirationSelection.Select(label, now));

    public void SelectCustomExpirationDate(
        DateTimeOffset selectedDate,
        TimeZoneInfo localTimeZone) =>
        SetExpirationSelection(_expirationSelection.SelectCustomDate(selectedDate, localTimeZone));

    // 从完整解密 Send 水合可编辑草稿(F1)。复制全部元数据字段,避免保存时把未加载字段覆盖为默认。
    public static SendEditorDraft FromExisting(Core.Models.Send send) => new()
    {
        Type = send.Type == Core.Enums.SendType.File ? SendType.File : SendType.Text,
        Name = send.Name,
        Text = send.Text?.Content ?? string.Empty,
        FileName = send.File?.FileName ?? string.Empty,
        Notes = send.Notes ?? string.Empty,
        HideTextByDefault = send.Text?.Hidden ?? false,
        HideEmail = send.HideEmail,
        Disabled = send.Disabled,
        MaxAccessCount = send.MaxAccessCount,
        ExpirationDate = send.ExpirationDate,
        DeletionDate = send.DeletionDate,
        // 绝对删除日期:标为"自定义"使 ToDeletionDate() 使用 DeletionDate(而非相对天数)。
        DeletionDateLabel = "自定义",
        WrappedKey = send.Key,
        ExistingHasPassword = send.HasPassword,
        ExistingAuthType = send.AuthType,
        ExistingRawAuthType = send.RawAuthType,
        // 编辑既有 Send 时默认不替换文件字节(只改元数据);仅当用户显式重选文件才置为非 null。
        ReplacementFile = null,
        // Plaintext password is never hydrated. Blank keeps protection for metadata updates, while
        // a protected file replacement must collect password input before creating the new Send.
    };

    partial void OnTypeChanged(SendType value)
    {
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsFile));
    }

    private void SetExpirationSelection(SendExpirationSelection selection)
    {
        if (_expirationSelection == selection)
            return;

        _expirationSelection = selection;
        OnPropertyChanged(nameof(ExpirationDate));
        OnPropertyChanged(nameof(ExpirationDateLabel));
        OnPropertyChanged(nameof(IsCustomExpiration));
        OnPropertyChanged(nameof(CustomExpirationDate));
        OnPropertyChanged(nameof(CustomExpirationDescription));
    }

    public bool HasRequiredData()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return false;

        return Type switch
        {
            SendType.Text => !string.IsNullOrWhiteSpace(Text),
            SendType.File => !string.IsNullOrWhiteSpace(FileName),
            _ => false,
        };
    }
}
