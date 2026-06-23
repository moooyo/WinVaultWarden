using App.ViewModels.Models;

namespace App.Services;

public interface ISendUiService
{
    IReadOnlyList<SendListItem> GetSends();
}

public sealed class MockSendUiService : ISendUiService
{
    public IReadOnlyList<SendListItem> GetSends() => new List<SendListItem>
    {
        new("s1", "项目周报.pdf", SendType.File, "2026-07-01 截止", "https://vault.example/send/s1"),
        new("s2", "临时登录口令", SendType.Text, "2026-06-25 截止", "https://vault.example/send/s2"),
        new("s3", "设计稿打包.zip", SendType.File, "无截止日期", "https://vault.example/send/s3"),
    };
}
