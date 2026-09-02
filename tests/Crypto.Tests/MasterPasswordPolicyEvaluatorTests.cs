using Core.Models;
using Crypto.PasswordStrength;
using Xunit;

namespace Crypto.Tests;

public sealed class MasterPasswordPolicyEvaluatorTests
{
    private readonly PasswordStrengthEvaluator _strength = new(
        new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())));

    [Fact]
    public void CaptureEvidence_CapturesOfficialCharacterAndLengthSemantics()
    {
        var evaluator = CreateEvaluator();

        var evidence = evaluator.CaptureEvidence("Éa😀1^");

        Assert.Equal(6, evidence.Utf16Length);
        Assert.True(evidence.HasUpper);
        Assert.True(evidence.HasLower);
        Assert.True(evidence.HasNumber);
        Assert.True(evidence.HasSpecial);
    }

    [Fact]
    public void CaptureEvidence_UsesExistingPasswordStrengthScore()
    {
        const string password = "Tr0ub4dour&3xpling-Z9q";
        var evaluator = CreateEvaluator();

        var evidence = evaluator.CaptureEvidence(password);

        Assert.Equal(_strength.Evaluate(password).Score, evidence.StrengthScore);
    }

    [Theory]
    [InlineData("١٢٣", false)]
    [InlineData("abc1", true)]
    [InlineData("abc+", false)]
    [InlineData("abc@", true)]
    [InlineData("abc_", false)]
    [InlineData("abc%", true)]
    public void CaptureEvidence_UsesOfficialAsciiNumberAndSpecialSets(
        string password,
        bool expected)
    {
        var evidence = CreateEvaluator().CaptureEvidence(password);

        if (password.Contains('١') || password.Contains('1'))
            Assert.Equal(expected, evidence.HasNumber);
        else
            Assert.Equal(expected, evidence.HasSpecial);
    }

    [Theory]
    [MemberData(nameof(RequirementCases))]
    public void Evaluate_AppliesEachOfficialRequirement(
        MasterPasswordPolicyRequirements requirements,
        MasterPasswordEvidence evidence,
        bool expected)
    {
        Assert.Equal(expected, CreateEvaluator().Evaluate(requirements, evidence));
    }

    public static TheoryData<MasterPasswordPolicyRequirements, MasterPasswordEvidence, bool>
        RequirementCases => new()
        {
            {
                new MasterPasswordPolicyRequirements(MinComplexity: 3),
                Evidence(score: 2),
                false
            },
            {
                new MasterPasswordPolicyRequirements(MinComplexity: 3),
                Evidence(score: 3),
                true
            },
            {
                new MasterPasswordPolicyRequirements(MinComplexity: 0),
                Evidence(score: 0),
                true
            },
            {
                new MasterPasswordPolicyRequirements(MinLength: 12),
                Evidence(length: 11),
                false
            },
            {
                new MasterPasswordPolicyRequirements(MinLength: 12),
                Evidence(length: 12),
                true
            },
            {
                new MasterPasswordPolicyRequirements(MinLength: 0),
                Evidence(length: 0),
                true
            },
            {
                new MasterPasswordPolicyRequirements(RequireUpper: true),
                Evidence(hasUpper: false),
                false
            },
            {
                new MasterPasswordPolicyRequirements(RequireLower: true),
                Evidence(hasLower: false),
                false
            },
            {
                new MasterPasswordPolicyRequirements(RequireNumbers: true),
                Evidence(hasNumber: false),
                false
            },
            {
                new MasterPasswordPolicyRequirements(RequireSpecial: true),
                Evidence(hasSpecial: false),
                false
            },
            {
                new MasterPasswordPolicyRequirements(
                    MinComplexity: 4,
                    MinLength: 128,
                    RequireUpper: true,
                    RequireLower: true,
                    RequireNumbers: true,
                    RequireSpecial: true,
                    EnforceOnLogin: true),
                Evidence(score: 4, length: 128),
                true
            },
        };

    [Theory]
    [InlineData(-1, null)]
    [InlineData(5, null)]
    [InlineData(null, 11)]
    [InlineData(null, 129)]
    public void Evaluate_InvalidProtocolValuesFailClosed(int? complexity, int? length)
    {
        var requirements = new MasterPasswordPolicyRequirements(complexity, length);

        Assert.False(CreateEvaluator().Evaluate(requirements, Evidence()));
    }

    [Theory]
    [InlineData(-1, 12)]
    [InlineData(5, 12)]
    [InlineData(4, -1)]
    public void Evaluate_InvalidEvidenceFailsClosed(int score, int length)
    {
        var evidence = Evidence(score: score, length: length);

        Assert.False(CreateEvaluator().Evaluate(new MasterPasswordPolicyRequirements(), evidence));
    }

    [Fact]
    public void Evaluate_MissingPolicyAcceptsWithoutInspectingEvidence()
    {
        var invalidEvidence = Evidence(score: -1, length: -1);

        Assert.True(CreateEvaluator().Evaluate(null, invalidEvidence));
    }

    [Fact]
    public void Evidence_DoesNotRetainOrRenderPlaintext()
    {
        const string secret = "NeverStoreThisMasterPassword!1";
        var evidence = CreateEvaluator().CaptureEvidence(secret);

        Assert.DoesNotContain(
            typeof(string),
            typeof(MasterPasswordEvidence).GetProperties().Select(property => property.PropertyType));
        Assert.DoesNotContain(secret, evidence.ToString(), StringComparison.Ordinal);
    }

    private MasterPasswordPolicyEvaluator CreateEvaluator() => new(_strength);

    private static MasterPasswordEvidence Evidence(
        int score = 4,
        int length = 128,
        bool hasUpper = true,
        bool hasLower = true,
        bool hasNumber = true,
        bool hasSpecial = true) =>
        new(score, length, hasUpper, hasLower, hasNumber, hasSpecial);
}
