using System.Text.Json;
using Core.Enums;
using Core.Models;
using Core.Session;

namespace Core.Services;

public static class MasterPasswordPolicyResolver
{
    private const int MaximumPolicyJsonCharacters = 32 * 1024;
    private static readonly HashSet<string> RecognizedFields = new(StringComparer.Ordinal)
    {
        "minComplexity",
        "minLength",
        "requireUpper",
        "requireLower",
        "requireNumbers",
        "requireSpecial",
        "enforceOnLogin",
    };

    public static MasterPasswordPolicyRequirements? Resolve(IVaultSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Resolve(snapshot.PolicyOrganizations, snapshot.Policies);
    }

    public static MasterPasswordPolicyRequirements? Resolve(
        IReadOnlyList<VaultOrganizationPolicyContext> organizations,
        IReadOnlyList<VaultOrganizationPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(policies);

        MasterPasswordPolicyRequirements? combined = null;
        foreach (var policy in policies)
        {
            if (!policy.Enabled
                || policy.Type != (int)OrganizationPolicyType.MasterPassword
                || !VaultPolicyEvaluator.PolicyApplies(
                    policy,
                    OrganizationPolicyType.MasterPassword,
                    organizations))
            {
                continue;
            }

            var requirements = Parse(policy.DataJson);
            combined = combined is null ? requirements : new MasterPasswordPolicyRequirements(
                Math.Max(combined.MinComplexity ?? 0, requirements.MinComplexity ?? 0),
                Math.Max(combined.MinLength ?? 0, requirements.MinLength ?? 0),
                combined.RequireUpper || requirements.RequireUpper,
                combined.RequireLower || requirements.RequireLower,
                combined.RequireNumbers || requirements.RequireNumbers,
                combined.RequireSpecial || requirements.RequireSpecial,
                combined.EnforceOnLogin || requirements.EnforceOnLogin);
        }

        return combined;
    }

    private static MasterPasswordPolicyRequirements Parse(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson)
            || dataJson.Length > MaximumPolicyJsonCharacters)
        {
            throw InvalidPolicy();
        }

        try
        {
            using var document = JsonDocument.Parse(dataJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw InvalidPolicy();

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!RecognizedFields.Contains(property.Name))
                {
                    if (RecognizedFields.Any(field =>
                            field.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw InvalidPolicy();
                    }
                    continue;
                }

                if (!fields.TryAdd(property.Name, property.Value))
                    throw InvalidPolicy();
            }

            var requirements = new MasterPasswordPolicyRequirements(
                ReadOptionalInteger(fields, "minComplexity"),
                ReadOptionalInteger(fields, "minLength"),
                ReadBoolean(fields, "requireUpper"),
                ReadBoolean(fields, "requireLower"),
                ReadBoolean(fields, "requireNumbers"),
                ReadBoolean(fields, "requireSpecial"),
                ReadBoolean(fields, "enforceOnLogin"));
            return requirements.HasValidProtocolValues ? requirements : throw InvalidPolicy();
        }
        catch (JsonException)
        {
            throw InvalidPolicy();
        }
    }

    private static int? ReadOptionalInteger(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw InvalidPolicy();
        return number;
    }

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidPolicy(),
        };
    }

    private static OrganizationPolicyException InvalidPolicy() => new(
        "The organization master password policy is invalid. Sync the vault and try again.");
}
