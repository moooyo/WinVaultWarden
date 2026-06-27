using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

// 破坏性操作的统一确认弹窗。各页/壳复用,保证交互与文案约定一致。
public static class DialogHelper
{
    public static async Task<bool> ConfirmAsync(
        XamlRoot root, string title, string message, string primaryText, string closeText = "取消")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
