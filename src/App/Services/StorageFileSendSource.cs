using System.IO;
using Core.Models;
using Windows.Storage;

namespace App.Services;

// 把用户在文件选择器里选中的 StorageFile 适配成前层第二层最终契约 SendFileSource(可重开明文源)。
// 关键不变式:
//   - 选择时只读取名称与长度(GetBasicPropertiesAsync),绝不把整份明文读进内存(不做整块缓冲/ToArray)。
//   - 上限(500 MiB)在构造 source、开任何流之前按明文口径校验,因此超限文件从不被打开。
//   - OpenReadAsync 每次返回一个从头开始的新只读流,供前层两遍式流加密(第一遍算 MAC、第二遍写密文)复用。
//   - 文件字节在两遍之间若被改成相同长度但不同内容,前层加密会 fail-closed 拒绝上传(见 SendCryptoService)。
public static class StorageFileSendSource
{
    public static async Task<SendFileSource> CreateAsync(StorageFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var properties = await file.GetBasicPropertiesAsync().AsTask(ct);
        var length = checked((long)properties.Size);
        SendFileSource.ValidateLength(length);
        return new SendFileSource(
            file.Name,
            length,
            token => OpenAsync(file, token));
    }

    private static async ValueTask<Stream> OpenAsync(StorageFile file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stream = await file.OpenStreamForReadAsync();
        if (ct.IsCancellationRequested)
        {
            await stream.DisposeAsync();
            ct.ThrowIfCancellationRequested();
        }
        return stream;
    }
}
