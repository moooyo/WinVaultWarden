using Core.Enums;
using Core.Models;
using Core.Services;
using Xunit;

namespace Vault.Tests;

public sealed class MasterPasswordPolicyResolverTests
{
    [Fact]
    public void Resolve_WithoutPoliciesReturnsNull()
    {
        Assert.Null(MasterPasswordPolicyResolver.Resolve(
            [Context("org")],
            Array.Empty<VaultOrganizationPolicy>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_MergesStrictestRequirementsForOwnersAndAdminsRegardlessOfOrder(
        bool reverseOrder)
    {
        var ownerPolicy = Policy(
            "owner-policy",
            "owner-org",
            """{"minComplexity":2,"minLength":32,"requireUpper":true,"requireNumbers":true,"enforceOnLogin":false}""");
        var adminPolicy = Policy(
            "admin-policy",
            "admin-org",
            """{"minComplexity":4,"minLength":20,"requireLower":true,"requireSpecial":true,"enforceOnLogin":true}""");
        var policies = reverseOrder
            ? new[] { adminPolicy, ownerPolicy }
            : new[] { ownerPolicy, adminPolicy };

        var result = MasterPasswordPolicyResolver.Resolve(
            [
                Context("owner-org", role: 0, status: 1),
                Context("admin-org", role: 1, status: 2),
            ],
            policies);

        Assert.Equal(
            new MasterPasswordPolicyRequirements(
                MinComplexity: 4,
                MinLength: 32,
                RequireUpper: true,
                RequireLower: true,
                RequireNumbers: true,
                RequireSpecial: true,
                EnforceOnLogin: true),
            result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Resolve_AppliesToAcceptedAndConfirmedMembers(int status)
    {
        var result = MasterPasswordPolicyResolver.Resolve(
            [Context("org", status: status)],
            [Policy("policy", "org", """{"minLength":24}""")]);

        Assert.NotNull(result);
        Assert.Equal(24, result.MinLength);
    }

    [Theory]
    [InlineData(0, true, true, false)]
    [InlineData(-1, true, true, false)]
    [InlineData(2, false, true, false)]
    [InlineData(2, true, false, false)]
    [InlineData(2, true, true, true)]
    public void Resolve_IgnoresIneligibleMembershipBeforeParsingData(
        int status,
        bool enabled,
        bool usePolicies,
        bool isProviderUser)
    {
        var result = MasterPasswordPolicyResolver.Resolve(
            [Context(
                "org",
                status: status,
                enabled: enabled,
                usePolicies: usePolicies,
                isProviderUser: isProviderUser)],
            [Policy("policy", "org", "invalid-json")]);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_IgnoresDisabledAndUnrelatedPoliciesBeforeParsingData()
    {
        var result = MasterPasswordPolicyResolver.Resolve(
            [Context("org")],
            [
                Policy("disabled", "org", "invalid-json", enabled: false),
                new VaultOrganizationPolicy(
                    "unrelated",
                    "org",
                    (int)OrganizationPolicyType.PasswordGenerator,
                    true,
                    "invalid-json"),
            ]);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_MissingOrAmbiguousMembershipStillEnforcesPolicy(bool ambiguous)
    {
        var organizations = ambiguous
            ? new[]
            {
                Context("org", enabled: false),
                Context("ORG", status: 0),
            }
            : new[] { Context("different-org") };

        var result = MasterPasswordPolicyResolver.Resolve(
            organizations,
            [Policy("policy", "org", """{"minLength":24,"requireUpper":true}""")]);

        Assert.Equal(
            new MasterPasswordPolicyRequirements(MinLength: 24, RequireUpper: true),
            result);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"minComplexity":null,"minLength":null}""")]
    [InlineData("""{"requireUpper":false,"requireLower":false,"requireNumbers":false,"requireSpecial":false,"enforceOnLogin":false}""")]
    public void Resolve_MissingFieldsAndNullableNumbersUseDefaults(string dataJson)
    {
        Assert.Equal(new MasterPasswordPolicyRequirements(), ResolveData(dataJson));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 12)]
    [InlineData(4, 128)]
    public void Resolve_PreservesValidNumericBoundaries(int complexity, int length)
    {
        var result = ResolveData(
            $$"""{"minComplexity":{{complexity}},"minLength":{{length}}}""");

        Assert.Equal(
            new MasterPasswordPolicyRequirements(MinComplexity: complexity, MinLength: length),
            result);
    }

    [Fact]
    public void Resolve_UnsetAndZeroValuesDoNotWeakenOtherPolicies()
    {
        var result = MasterPasswordPolicyResolver.Resolve(
            [Context("org")],
            [
                Policy("strict", "org", """{"minComplexity":3,"minLength":24,"requireUpper":true}"""),
                Policy("unset", "org", """{"minComplexity":null,"minLength":null}"""),
                Policy("zero", "org", """{"minComplexity":0,"minLength":0,"requireUpper":false}"""),
            ]);

        Assert.Equal(
            new MasterPasswordPolicyRequirements(
                MinComplexity: 3,
                MinLength: 24,
                RequireUpper: true),
            result);
    }

    [Fact]
    public void Resolve_UnknownFieldsDoNotHideKnownRequirements()
    {
        var result = ResolveData(
            """{"futureOption":{"enabled":true},"Object":"policy","other":null,"minLength":24,"requireUpper":true}""");

        Assert.Equal(
            new MasterPasswordPolicyRequirements(MinLength: 24, RequireUpper: true),
            result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("12")]
    [InlineData("\"policy\"")]
    [InlineData("invalid-json")]
    [InlineData("{")]
    [InlineData("""{"minLength":24,}""")]
    [InlineData("""{/* comment */ "minLength":24}""")]
    public void Resolve_InvalidRootOrJsonThrows(string? dataJson)
    {
        Assert.Throws<OrganizationPolicyException>(() => ResolveData(dataJson));
    }

    [Theory]
    [InlineData("""{"minComplexity":-1}""")]
    [InlineData("""{"minComplexity":5}""")]
    [InlineData("""{"minComplexity":1.5}""")]
    [InlineData("""{"minComplexity":"3"}""")]
    [InlineData("""{"minComplexity":true}""")]
    [InlineData("""{"minComplexity":2147483648}""")]
    [InlineData("""{"minLength":-1}""")]
    [InlineData("""{"minLength":1}""")]
    [InlineData("""{"minLength":11}""")]
    [InlineData("""{"minLength":129}""")]
    [InlineData("""{"minLength":12.5}""")]
    [InlineData("""{"minLength":"24"}""")]
    [InlineData("""{"minLength":true}""")]
    [InlineData("""{"minLength":2147483648}""")]
    public void Resolve_InvalidNumericTypeOrRangeThrows(string dataJson)
    {
        Assert.Throws<OrganizationPolicyException>(() => ResolveData(dataJson));
    }

    [Theory]
    [MemberData(nameof(InvalidBooleanCases))]
    public void Resolve_NullOrWrongTypedBooleansThrow(string dataJson)
    {
        Assert.Throws<OrganizationPolicyException>(() => ResolveData(dataJson));
    }

    public static TheoryData<string> InvalidBooleanCases
    {
        get
        {
            var cases = new TheoryData<string>();
            foreach (var propertyName in BooleanFields)
            {
                foreach (var value in new[] { "null", "\"true\"", "1", "{}", "[]" })
                    cases.Add($$"""{"{{propertyName}}":{{value}}}""");
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(AmbiguousFieldCases))]
    public void Resolve_DuplicateOrWrongCaseKnownFieldsThrow(string dataJson)
    {
        Assert.Throws<OrganizationPolicyException>(() => ResolveData(dataJson));
    }

    public static TheoryData<string> AmbiguousFieldCases
    {
        get
        {
            var cases = new TheoryData<string>();
            foreach (var propertyName in new[] { "minComplexity", "minLength" }.Concat(BooleanFields))
            {
                var value = propertyName.StartsWith("min", StringComparison.Ordinal) ? "0" : "false";
                var wrongCase = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
                cases.Add($$"""{"{{propertyName}}":{{value}},"{{propertyName}}":{{value}}}""");
                cases.Add($$"""{"{{wrongCase}}":{{value}}}""");
            }
            return cases;
        }
    }

    [Fact]
    public void Resolve_ExcessiveJsonLengthOrDepthThrows()
    {
        var oversized = new string(' ', 32 * 1024) + "{}";
        var deeplyNested = "{\"future\":" + new string('[', 16) + "0" + new string(']', 16) + "}";

        Assert.Throws<OrganizationPolicyException>(() => ResolveData(oversized));
        Assert.Throws<OrganizationPolicyException>(() => ResolveData(deeplyNested));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_ValidPolicyCannotHideMalformedApplicablePolicy(bool invalidFirst)
    {
        var valid = Policy("valid", "org", """{"minLength":24}""");
        var invalid = Policy("invalid", "org", """{"requireUpper":null}""");
        var policies = invalidFirst
            ? new[] { invalid, valid }
            : new[] { valid, invalid };

        Assert.Throws<OrganizationPolicyException>(() =>
            MasterPasswordPolicyResolver.Resolve([Context("org")], policies));
    }

    private static readonly string[] BooleanFields =
    [
        "requireUpper",
        "requireLower",
        "requireNumbers",
        "requireSpecial",
        "enforceOnLogin",
    ];

    private static MasterPasswordPolicyRequirements? ResolveData(string? dataJson) =>
        MasterPasswordPolicyResolver.Resolve(
            [Context("org")],
            [Policy("policy", "org", dataJson!)]);

    private static VaultOrganizationPolicyContext Context(
        string organizationId,
        int role = 2,
        int status = 2,
        bool enabled = true,
        bool usePolicies = true,
        bool isProviderUser = false) =>
        new(
            organizationId,
            Status: status,
            Role: role,
            Enabled: enabled,
            UsePolicies: usePolicies,
            IsProviderUser: isProviderUser);

    private static VaultOrganizationPolicy Policy(
        string id,
        string organizationId,
        string dataJson,
        bool enabled = true) =>
        new(
            id,
            organizationId,
            (int)OrganizationPolicyType.MasterPassword,
            enabled,
            dataJson);
}
