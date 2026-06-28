namespace Core.Models;

// 解密后的附件元数据。明文 FileName 只允许存在于内存中。
public sealed record CipherAttachment(string Id, string FileName, long Size, string SizeName);
