using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Vault;

namespace App.Services;

/// <summary>
/// Stores account-scoped usage metadata without persisting cipher identifiers or vault content.
/// </summary>
public sealed class VaultRecentUseStore
{
    private const int FileSignature = 0x31525556;
    private const int MaximumRecords = 5000;
    private const int HashLength = 32;
    private const int HeaderLength = sizeof(int) * 2;
    private const int RecordLength = HashLength + sizeof(long);
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);
    private readonly string _scopeHash;
    private readonly string? _filePath;

    /// <summary>
    /// Creates a store for an unambiguous scope containing the account and all server endpoints.
    /// Prefer <see cref="CreateForSession"/> when a live session is available.
    /// </summary>
    public VaultRecentUseStore(string accountScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountScope);
        _scopeHash = Hash(accountScope);
        _filePath = CreateFilePath(_scopeHash);
        LoadFromDisk();
    }

    public static VaultRecentUseStore? CreateForSession(VaultSession? session)
    {
        if (session is null)
            return null;

        try
        {
            var lease = session.CaptureLease();
            var endpoints = lease.Endpoints;
            var scope = new StringBuilder();
            foreach (var part in new[]
            {
                lease.AccountId,
                endpoints.WebVault.Value,
                endpoints.Api.Value,
                endpoints.Identity.Value,
                endpoints.Notifications.Value,
                endpoints.Icons.Value,
                endpoints.SendWeb.Value,
            })
            {
                // Length prefixes keep embedded separators from creating ambiguous scopes.
                scope.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
            }

            return new VaultRecentUseStore(scope.ToString());
        }
        catch (InvalidOperationException)
        {
            // Logged-out and demonstration sessions do not have a persistent account scope.
            return null;
        }
    }

    public DateTimeOffset? GetLastUsed(string cipherId)
    {
        if (!IsValidCipherId(cipherId))
            return null;

        var key = HashCipherId(cipherId);
        lock (_gate)
            return _lastUsed.TryGetValue(key, out var usedAt) ? usedAt : null;
    }

    public void Record(string cipherId, DateTimeOffset usedAt)
    {
        if (!IsValidCipherId(cipherId) || !IsValidTimestamp(usedAt.UtcTicks))
            return;

        var key = HashCipherId(cipherId);
        lock (_gate)
        {
            if (_lastUsed.TryGetValue(key, out var previous) && previous >= usedAt)
                return;

            _lastUsed[key] = usedAt.ToUniversalTime();
            if (_lastUsed.Count > MaximumRecords)
            {
                var oldest = _lastUsed.MinBy(static entry => entry.Value);
                _lastUsed.Remove(oldest.Key);
            }

            SaveToDisk();
        }
    }

    private void LoadFromDisk()
    {
        if (_filePath is null)
            return;

        try
        {
            using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length < HeaderLength
                || stream.Length > HeaderLength + MaximumRecords * RecordLength)
                return;

            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != FileSignature)
                return;

            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumRecords
                || stream.Length != HeaderLength + count * RecordLength)
                return;

            var loaded = new Dictionary<string, DateTimeOffset>(count, StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                var hash = reader.ReadBytes(HashLength);
                if (hash.Length != HashLength)
                    return;

                var ticks = reader.ReadInt64();
                if (!IsValidTimestamp(ticks))
                    continue;

                var key = Convert.ToHexString(hash);
                var usedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
                if (!loaded.TryGetValue(key, out var previous) || usedAt > previous)
                    loaded[key] = usedAt;
            }

            foreach (var entry in loaded)
                _lastUsed[entry.Key] = entry.Value;
        }
        catch (Exception exception) when (IsPersistenceException(exception))
        {
            // Optional local metadata must never prevent the vault from opening.
        }
    }

    private void SaveToDisk()
    {
        if (_filePath is null)
            return;

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
                writer.Write(FileSignature);
                writer.Write(_lastUsed.Count);
                foreach (var entry in _lastUsed)
                {
                    writer.Write(Convert.FromHexString(entry.Key));
                    writer.Write(entry.Value.UtcTicks);
                }

                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // The temporary file shares the destination directory for atomic replacement.
            if (File.Exists(_filePath))
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _filePath);
        }
        catch (Exception exception) when (IsPersistenceException(exception))
        {
            // Keep the in-memory update even when persistence is unavailable.
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (IsPersistenceException(exception))
                {
                    // Temporary-file cleanup is best effort after an interrupted write.
                }
            }
        }
    }

    private string HashCipherId(string cipherId) => Hash(_scopeHash + ":" + cipherId);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsValidCipherId(string? cipherId) =>
        !string.IsNullOrWhiteSpace(cipherId) && cipherId.Length <= 4096;

    private static bool IsValidTimestamp(long utcTicks) =>
        utcTicks >= DateTimeOffset.UnixEpoch.UtcTicks
        && utcTicks <= DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks;

    private static string? CreateFilePath(string scopeHash)
    {
        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localData)
                ? null
                : Path.Combine(localData, "WinVaultWarden", "recent-use", scopeHash + ".bin");
        }
        catch (Exception exception) when (IsPersistenceException(exception))
        {
            return null;
        }
    }

    private static bool IsPersistenceException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException;
}
