using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Media.Imaging;
using QRCoder;
using Windows.Storage.Streams;

namespace App.Services;

internal static class TotpQrCodeRenderer
{
    public static async Task<BitmapImage> RenderAsync(
        string otpauthUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(otpauthUri);
        cancellationToken.ThrowIfCancellationRequested();

        using var data = QRCodeGenerator.GenerateQrCode(
            otpauthUri,
            QRCodeGenerator.ECCLevel.M);
        using var renderer = new PngByteQRCode(data);
        var pngBytes = renderer.GetGraphic(pixelsPerModule: 8, drawQuietZones: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync().AsTask(cancellationToken);
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return bitmap;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pngBytes);
        }
    }
}
