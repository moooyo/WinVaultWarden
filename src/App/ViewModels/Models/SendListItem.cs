namespace App.ViewModels.Models;

public enum SendType { Text, File }

public enum SendReconciliationState
{
    None,
    CommittedPendingRefresh,
    ReplacementOriginalPending,
    ReplacementNewPending,
}

public sealed record SendListItem(
    string Id,
    string Name,
    SendType Type,
    string DeleteDate,
    string? Link,
    SendReconciliationState ReconciliationState = SendReconciliationState.None)
{
    public string TypeLabel
    {
        get
        {
            var type = Type == SendType.File ? "文件" : "文本";
            return ReconciliationState switch
            {
                SendReconciliationState.CommittedPendingRefresh => $"{type} · 已保存，等待刷新",
                SendReconciliationState.ReplacementOriginalPending => $"{type} · 原 Send，删除结果待确认",
                SendReconciliationState.ReplacementNewPending => $"{type} · 新 Send，等待对账",
                _ => type,
            };
        }
    }

    public bool CanModify => ReconciliationState == SendReconciliationState.None;

    public string DeleteDateAccessibleLabel => $"删除日期 {DeleteDate}";

    public string Glyph => Type switch
    {
        SendType.Text => "\uE8D2",
        SendType.File => "\uE8A5",
        _ => "\uE724",
    };
}

public sealed record SendDeleteRequest(
    SendListItem Item,
    Core.Session.VaultSessionLease OriginatingSession);
