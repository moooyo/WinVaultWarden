namespace App.ViewModels.Models;

// 详情区附件行的展示模型。Id 用于下载/删除定位,FileName 为已解密文件名,SizeName 为服务端给的人类可读大小。
public sealed partial record AttachmentItem(string Id, string FileName, string SizeName)
{
    public string Glyph => global::App.Services.AttachmentGlyph.ForFileName(FileName);
    public string AccessibleLabel => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextCCD5A5A18F4F21DF,FileName,SizeName);
}
