using Core.Abstractions;
using Core.Services;
using System.Security.Cryptography;
using System.Text;

namespace App.Services;

public sealed class PersistentDeviceIdentifierProvider : IDeviceIdentifierProvider
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ITokenStore _tokenStore;

    public PersistentDeviceIdentifierProvider(ITokenStore tokenStore)
        : this(
            tokenStore,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinVaultWarden",
                "device-id"))
    {
    }

    internal PersistentDeviceIdentifierProvider(ITokenStore tokenStore, string path)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string GetDeviceIdentifier()
    {
        lock (_gate)
        {
            if (TryRead(out var identifier))
                return identifier;

            identifier = _tokenStore.TryLoad(out var persisted)
                && IsCanonicalIdentifier(persisted.DeviceIdentifier)
                    ? persisted.DeviceIdentifier
                    : Guid.NewGuid().ToString("D");
            Write(identifier);
            return identifier;
        }
    }

    private bool TryRead(out string identifier)
    {
        identifier = string.Empty;
        try
        {
            if (!File.Exists(_path))
                return false;
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > 64)
                return false;
            var bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            string value;
            try
            {
                stream.ReadExactly(bytes);
                value = Encoding.ASCII.GetString(bytes).Trim();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            if (!IsCanonicalIdentifier(value))
                return false;
            identifier = value;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Write(string identifier)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("The device identifier path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var bytes = Encoding.ASCII.GetBytes(identifier);
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool IsCanonicalIdentifier(string? value) =>
        Guid.TryParseExact(value, "D", out var identifier)
        && identifier != Guid.Empty;
}
