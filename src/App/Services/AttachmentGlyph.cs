namespace App.Services;

// 按文件扩展名返回 Segoe Fluent Icons glyph(纯装饰,码点为实现细节)。
public static class AttachmentGlyph
{
    private const string Generic = "";  // Attach(回形针,通用/未知)
    private const string Image   = "";  // Photo
    private const string Pdf     = "";  // OpenFile 型
    private const string Doc     = "";  // Document
    private const string Sheet   = "";  // 表格
    private const string Archive = "";  // 压缩包
    private const string Audio   = "";  // MyMusic
    private const string Video   = "";  // 媒体

    public static string ForFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return Generic;
        int dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1) return Generic;   // 无扩展 / 结尾点
        var ext = fileName[(dot + 1)..].ToLowerInvariant();
        return ext switch
        {
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "svg" or "heic" => Image,
            "pdf" => Pdf,
            "doc" or "docx" or "txt" or "rtf" or "md" or "odt" => Doc,
            "xls" or "xlsx" or "csv" or "ods" => Sheet,
            "zip" or "rar" or "7z" or "gz" or "tar" => Archive,
            "mp3" or "wav" or "flac" or "aac" or "ogg" or "m4a" => Audio,
            "mp4" or "mov" or "avi" or "mkv" or "webm" => Video,
            _ => Generic,
        };
    }
}
