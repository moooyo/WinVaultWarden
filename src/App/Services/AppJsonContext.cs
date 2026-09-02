using System.Text.Json.Serialization;
using Core.Models;

namespace App.Services;

// Source-generated metadata for DPAPI storage and local preferences.
// Indented output keeps the existing human-readable preference format.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PersistedSession))]
[JsonSerializable(typeof(PersistedAccountCryptographicState))]
[JsonSerializable(typeof(PersistedSessionCollection))]
[JsonSerializable(typeof(PersistedSessionIndex))]
[JsonSerializable(typeof(PersistedSessionBlobEnvelope))]
[JsonSerializable(typeof(PersistedDeviceKeyLedger))]
[JsonSerializable(typeof(PersistedCipherCreatePendingLedger))]
[JsonSerializable(typeof(PersistedTrustedDeviceApprovalLedger))]
[JsonSerializable(typeof(PersistedTdeV2JitPendingLedger))]
[JsonSerializable(typeof(PersistedLoginWithDeviceQuarantineLedger))]
[JsonSerializable(typeof(PersistedAccountDeletionTombstoneLedger))]
[JsonSerializable(typeof(VaultCacheCollection))]
[JsonSerializable(typeof(ServerEndpoints))]
[JsonSerializable(typeof(AppPreferencesData))]
[JsonSerializable(typeof(SavedSearchViewData))]
[JsonSerializable(typeof(List<SavedSearchViewData>))]
[JsonSerializable(typeof(Core.Models.VaultCacheEntry))]
internal partial class AppJsonContext : JsonSerializerContext;
