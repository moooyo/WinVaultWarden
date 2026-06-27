using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

public class SendViewModelTests
{
    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public int SecretCount { get; private set; }
        public int PlainCount { get; private set; }

        public void SetText(string text) { Text = text; PlainCount++; }
        public void SetSecretText(string text, int autoClearSeconds = 30) { Text = text; SecretCount++; }
    }

    [Fact]
    public void CopyLink_UsesSecretClipboard()
    {
        var clipboard = new RecordingClipboard();
        var vm = new SendViewModel(new MockSendUiService(), clipboard);
        var item = vm.FilteredItems[0];

        vm.CopyLinkCommand.Execute(item);

        Assert.Equal(item.Link, clipboard.Text);
        Assert.Equal(1, clipboard.SecretCount);
        Assert.Equal(0, clipboard.PlainCount);
    }
}
