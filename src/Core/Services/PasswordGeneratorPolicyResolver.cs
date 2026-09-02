using System.Text.Json;
using Core.Enums;
using Core.Models;
using Core.Session;

namespace Core.Services;

public static class PasswordGeneratorPolicyResolver
{
    private const int MaximumPolicyJsonCharacters = 32 * 1024;
    private const int MaximumPasswordLength = 128;
    private const int MaximumCharacterMinimum = 128;
    private const int MaximumPassphraseWords = 20;

    private static readonly HashSet<string> RecognizedFields = new(StringComparer.Ordinal)
    {
        "overridePasswordType",
        "minLength",
        "useUpper",
        "useLower",
        "useNumbers",
        "minNumbers",
        "useSpecial",
        "minSpecial",
        "minNumberWords",
        "capitalize",
        "includeNumber",
    };

    public static PasswordGeneratorPolicyRequirements Resolve(IVaultSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Resolve(snapshot.PolicyOrganizations, snapshot.Policies);
    }

    public static PasswordGeneratorPolicyRequirements Resolve(
        IReadOnlyList<VaultOrganizationPolicyContext> organizations,
        IReadOnlyList<VaultOrganizationPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(policies);

        var applicableCount = 0;
        var hasMalformedData = false;
        var minLength = 0;
        var useUppercase = false;
        var useLowercase = false;
        var useNumbers = false;
        var minNumbers = 0;
        var useSpecial = false;
        var minSpecial = 0;
        var minNumberWords = 0;
        var capitalize = false;
        var includeNumber = false;
        PasswordGeneratorCredentialType? overridePasswordType = null;

        foreach (var policy in policies)
        {
            if (!VaultPolicyEvaluator.IsEnabledForUser(
                    OrganizationPolicyType.PasswordGenerator,
                    organizations,
                    [policy]))
            {
                continue;
            }

            applicableCount++;
            var parsed = Parse(policy.DataJson);
            hasMalformedData |= parsed.IsMalformed;
            minLength = Math.Max(minLength, parsed.MinLength);
            useUppercase |= parsed.UseUppercase;
            useLowercase |= parsed.UseLowercase;
            useNumbers |= parsed.UseNumbers;
            minNumbers = Math.Max(minNumbers, parsed.MinNumbers);
            useSpecial |= parsed.UseSpecial;
            minSpecial = Math.Max(minSpecial, parsed.MinSpecial);
            minNumberWords = Math.Max(minNumberWords, parsed.MinNumberWords);
            capitalize |= parsed.Capitalize;
            includeNumber |= parsed.IncludeNumber;

            if (parsed.OverridePasswordType == PasswordGeneratorCredentialType.Password
                || overridePasswordType is null)
            {
                overridePasswordType = parsed.OverridePasswordType ?? overridePasswordType;
            }
        }

        var requirements = new PasswordGeneratorPolicyRequirements(
            applicableCount,
            hasMalformedData,
            minLength,
            useUppercase,
            useLowercase,
            useNumbers,
            minNumbers,
            useSpecial,
            minSpecial,
            minNumberWords,
            capitalize,
            includeNumber,
            overridePasswordType);
        return requirements.PasswordRequirementsAreSatisfiable
            ? requirements
            : requirements with { HasMalformedPolicyData = true };
    }

    private static ParsedPolicy Parse(string? dataJson)
    {
        if (string.Equals(dataJson?.Trim(), "null", StringComparison.Ordinal))
            return ParsedPolicy.Empty;

        if (string.IsNullOrWhiteSpace(dataJson)
            || dataJson.Length > MaximumPolicyJsonCharacters)
        {
            return ParsedPolicy.Malformed;
        }

        try
        {
            using var document = JsonDocument.Parse(dataJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind == JsonValueKind.Null)
                return ParsedPolicy.Empty;
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return ParsedPolicy.Malformed;

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var malformed = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!RecognizedFields.Contains(property.Name))
                {
                    if (RecognizedFields.Any(field =>
                            field.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        malformed = true;
                    }
                    continue;
                }

                if (!fields.TryAdd(property.Name, property.Value))
                {
                    malformed = true;
                }
            }

            var minLength = ReadBoundedInteger(
                fields,
                "minLength",
                5,
                MaximumPasswordLength,
                ref malformed);
            var minNumbers = ReadBoundedInteger(
                fields,
                "minNumbers",
                0,
                MaximumCharacterMinimum,
                ref malformed);
            var minSpecial = ReadBoundedInteger(
                fields,
                "minSpecial",
                0,
                MaximumCharacterMinimum,
                ref malformed);
            var minNumberWords = ReadBoundedInteger(
                fields,
                "minNumberWords",
                3,
                MaximumPassphraseWords,
                ref malformed);
            var useUppercase = ReadBoolean(fields, "useUpper", ref malformed);
            var useLowercase = ReadBoolean(fields, "useLower", ref malformed);
            var useNumbers = ReadBoolean(fields, "useNumbers", ref malformed);
            var useSpecial = ReadBoolean(fields, "useSpecial", ref malformed);
            var capitalize = ReadBoolean(fields, "capitalize", ref malformed);
            var includeNumber = ReadBoolean(fields, "includeNumber", ref malformed);
            var overridePasswordType = ReadOverride(fields, ref malformed);

            return new ParsedPolicy(
                malformed,
                minLength,
                useUppercase,
                useLowercase,
                useNumbers,
                minNumbers,
                useSpecial,
                minSpecial,
                minNumberWords,
                capitalize,
                includeNumber,
                overridePasswordType);
        }
        catch (JsonException)
        {
            return ParsedPolicy.Malformed;
        }
    }

    private static int ReadBoundedInteger(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        int positiveMinimum,
        int maximum,
        ref bool malformed)
    {
        if (!fields.TryGetValue(name, out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || (value != 0 && value < positiveMinimum)
            || value > maximum)
        {
            malformed = true;
            return 0;
        }

        return value;
    }

    private static bool ReadBoolean(
        IReadOnlyDictionary<string, JsonElement> fields,
        string name,
        ref bool malformed)
    {
        if (!fields.TryGetValue(name, out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return element.GetBoolean();

        malformed = true;
        return false;
    }

    private static PasswordGeneratorCredentialType? ReadOverride(
        IReadOnlyDictionary<string, JsonElement> fields,
        ref bool malformed)
    {
        if (!fields.TryGetValue("overridePasswordType", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            malformed = true;
            return null;
        }

        return element.GetString() switch
        {
            null or "" => null,
            "password" => PasswordGeneratorCredentialType.Password,
            "passphrase" => PasswordGeneratorCredentialType.Passphrase,
            _ => MarkMalformedOverride(ref malformed),
        };
    }

    private static PasswordGeneratorCredentialType? MarkMalformedOverride(ref bool malformed)
    {
        malformed = true;
        return null;
    }

    private sealed record ParsedPolicy(
        bool IsMalformed,
        int MinLength,
        bool UseUppercase,
        bool UseLowercase,
        bool UseNumbers,
        int MinNumbers,
        bool UseSpecial,
        int MinSpecial,
        int MinNumberWords,
        bool Capitalize,
        bool IncludeNumber,
        PasswordGeneratorCredentialType? OverridePasswordType)
    {
        public static ParsedPolicy Empty { get; } = new(
            false, 0, false, false, false, 0, false, 0, 0, false, false, null);

        public static ParsedPolicy Malformed { get; } = Empty with { IsMalformed = true };
    }
}
