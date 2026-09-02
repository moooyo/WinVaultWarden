using Core.Enums;
using Core.Models;
using Core.Session;
using System.Text.Json;

namespace Core.Services;

public enum MaximumVaultTimeoutMode
{
    Never,
    OnAppRestart,
    OnSystemLock,
    Minutes,
    Immediately,
}

public enum MaximumVaultTimeoutAction
{
    Lock,
    Logout,
}

public sealed record MaximumVaultTimeoutConstraint(
    bool Applies,
    MaximumVaultTimeoutMode Mode,
    int Minutes,
    MaximumVaultTimeoutAction? RequiredAction,
    bool UsedFailClosedFallback)
{
    public static MaximumVaultTimeoutConstraint None { get; } = new(
        Applies: false,
        Mode: MaximumVaultTimeoutMode.Never,
        Minutes: 0,
        RequiredAction: null,
        UsedFailClosedFallback: false);
}

public static class MaximumVaultTimeoutPolicyResolver
{
    public const int FailClosedFallbackMinutes = 15;

    public static MaximumVaultTimeoutConstraint Resolve(IVaultSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Resolve(snapshot.PolicyOrganizations, snapshot.Policies);
    }

    public static MaximumVaultTimeoutConstraint Resolve(
        IReadOnlyList<VaultOrganizationPolicyContext> organizations,
        IReadOnlyList<VaultOrganizationPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(organizations);
        ArgumentNullException.ThrowIfNull(policies);

        ParsedPolicy? strictest = null;
        var usedFallback = false;
        MaximumVaultTimeoutAction? requiredAction = null;

        foreach (var policy in policies)
        {
            if (!policy.Enabled
                || policy.Type != (int)OrganizationPolicyType.MaximumVaultTimeout
                || !VaultPolicyEvaluator.PolicyApplies(
                    policy,
                    OrganizationPolicyType.MaximumVaultTimeout,
                    organizations))
            {
                continue;
            }

            var parsed = Parse(policy.DataJson);
            usedFallback |= parsed.UsedFallback;
            if (strictest is null || IsStricter(parsed, strictest.Value))
                strictest = parsed;

            requiredAction = MergeActions(requiredAction, parsed.RequiredAction);
        }

        if (strictest is null)
            return MaximumVaultTimeoutConstraint.None;

        return new MaximumVaultTimeoutConstraint(
            Applies: true,
            Mode: strictest.Value.Mode,
            Minutes: strictest.Value.Minutes,
            RequiredAction: requiredAction,
            UsedFailClosedFallback: usedFallback);
    }

    private static ParsedPolicy Parse(string dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
            return Fallback();

        try
        {
            using var document = JsonDocument.Parse(dataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fallback();

            var action = ParseAction(root);
            if (HasAmbiguousContractProperties(root))
                return Fallback(action);

            string? type = null;
            if (root.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind != JsonValueKind.Null)
            {
                if (typeElement.ValueKind != JsonValueKind.String)
                    return Fallback(action);
                type = typeElement.GetString();
            }

            return type switch
            {
                "immediately" => new ParsedPolicy(
                    Mode: MaximumVaultTimeoutMode.Immediately,
                    Minutes: 0,
                    RequiredAction: action,
                    UsedFallback: false),
                "onSystemLock" => new ParsedPolicy(
                    Mode: MaximumVaultTimeoutMode.OnSystemLock,
                    Minutes: 0,
                    RequiredAction: action,
                    UsedFallback: false),
                "onAppRestart" => new ParsedPolicy(
                    Mode: MaximumVaultTimeoutMode.OnAppRestart,
                    Minutes: 0,
                    RequiredAction: action,
                    UsedFallback: false),
                "never" => new ParsedPolicy(
                    Mode: MaximumVaultTimeoutMode.Never,
                    Minutes: 0,
                    RequiredAction: action,
                    UsedFallback: false),
                "custom" or null => ParseMinutes(root, action),
                _ => Fallback(action),
            };
        }
        catch (JsonException)
        {
            return Fallback();
        }
        catch (InvalidOperationException)
        {
            return Fallback();
        }
    }

    private static ParsedPolicy ParseMinutes(
        JsonElement root,
        MaximumVaultTimeoutAction? action)
    {
        if (root.TryGetProperty("minutes", out var minutesElement)
            && minutesElement.ValueKind == JsonValueKind.Number
            && minutesElement.TryGetInt32(out var minutes)
            && minutes > 0)
        {
            return new ParsedPolicy(
                Mode: MaximumVaultTimeoutMode.Minutes,
                Minutes: minutes,
                RequiredAction: action,
                UsedFallback: false);
        }

        return Fallback(action);
    }

    private static MaximumVaultTimeoutAction? ParseAction(JsonElement root)
    {
        if (!root.TryGetProperty("action", out var actionElement)
            || actionElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (actionElement.ValueKind != JsonValueKind.String)
            return null;

        return actionElement.GetString() switch
        {
            "lock" => MaximumVaultTimeoutAction.Lock,
            "logOut" => MaximumVaultTimeoutAction.Logout,
            _ => null,
        };
    }

    private static bool HasAmbiguousContractProperties(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            var canonicalName = property.Name switch
            {
                "type" => "type",
                "minutes" => "minutes",
                "action" => "action",
                _ when property.Name.Equals("type", StringComparison.OrdinalIgnoreCase) => "type",
                _ when property.Name.Equals("minutes", StringComparison.OrdinalIgnoreCase) => "minutes",
                _ when property.Name.Equals("action", StringComparison.OrdinalIgnoreCase) => "action",
                _ => null,
            };
            if (canonicalName is null)
                continue;
            if (!property.Name.Equals(canonicalName, StringComparison.Ordinal)
                || !seen.Add(canonicalName))
            {
                return true;
            }
        }

        return false;
    }

    private static ParsedPolicy Fallback(
        MaximumVaultTimeoutAction? action = null) => new(
        Mode: MaximumVaultTimeoutMode.Minutes,
        Minutes: FailClosedFallbackMinutes,
        RequiredAction: action,
        UsedFallback: true);

    private static bool IsStricter(ParsedPolicy candidate, ParsedPolicy current)
    {
        var candidateRank = StrictnessRank(candidate.Mode);
        var currentRank = StrictnessRank(current.Mode);
        if (candidateRank != currentRank)
            return candidateRank < currentRank;

        return candidate.Mode == MaximumVaultTimeoutMode.Minutes
            && candidate.Minutes < current.Minutes;
    }

    private static int StrictnessRank(MaximumVaultTimeoutMode mode) => mode switch
    {
        MaximumVaultTimeoutMode.Immediately => 0,
        MaximumVaultTimeoutMode.Minutes => 1,
        MaximumVaultTimeoutMode.OnSystemLock => 2,
        MaximumVaultTimeoutMode.OnAppRestart => 3,
        MaximumVaultTimeoutMode.Never => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static MaximumVaultTimeoutAction? MergeActions(
        MaximumVaultTimeoutAction? current,
        MaximumVaultTimeoutAction? candidate)
    {
        if (current == MaximumVaultTimeoutAction.Logout
            || candidate == MaximumVaultTimeoutAction.Logout)
        {
            return MaximumVaultTimeoutAction.Logout;
        }

        return current ?? candidate;
    }

    private readonly record struct ParsedPolicy(
        MaximumVaultTimeoutMode Mode,
        int Minutes,
        MaximumVaultTimeoutAction? RequiredAction,
        bool UsedFallback);
}
