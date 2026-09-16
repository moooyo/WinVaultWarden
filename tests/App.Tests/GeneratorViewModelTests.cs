using App.Services;
using App.ViewModels;
using Core.Enums;
using Core.Models;
using Crypto;
using Crypto.PasswordStrength;
using Vault;
using Xunit;

namespace App.Tests;

public class GeneratorViewModelTests
{
    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public int SecretCount { get; private set; }
        public int PlainCount { get; private set; }

        public void SetText(string text) { Text = text; PlainCount++; }
        public void SetSecretText(string text, int? autoClearSeconds = null) { Text = text; SecretCount++; }
    }

    [Fact]
    public void Constructor_GeneratesDefaultPassword()
    {
        var vm = new GeneratorViewModel();

        Assert.Equal(14, vm.GeneratedPassword.Length);
        Assert.Contains(vm.GeneratedPassword, char.IsLower);
        Assert.Contains(vm.GeneratedPassword, char.IsUpper);
        Assert.Contains(vm.GeneratedPassword, char.IsDigit);
    }

    [Fact]
    public void CopyCommand_CopiesGeneratedPassword()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard);

        vm.CopyCommand.Execute(null);

        Assert.Equal(vm.GeneratedPassword, clipboard.Text);
    }

    [Fact]
    public void DisablingEveryCharacterSet_KeepsLowercaseEnabled()
    {
        var vm = new GeneratorViewModel
        {
            IncludeUppercase = false,
            IncludeLowercase = false,
            IncludeNumbers = false,
            IncludeSpecial = false,
        };

        Assert.True(vm.IncludeLowercase);
        Assert.Equal(14, vm.GeneratedPassword.Length);
    }

    [Fact]
    public void MinimumsGreaterThanLength_AreReducedBeforeGeneration()
    {
        var vm = new GeneratorViewModel
        {
            Length = 5,
            IncludeUppercase = true,
            IncludeLowercase = true,
            IncludeNumbers = true,
            IncludeSpecial = true,
            MinNumbers = 8,
            MinSpecial = 8,
        };

        vm.RegenerateCommand.Execute(null);

        Assert.Equal(5, vm.GeneratedPassword.Length);
        Assert.Equal(0, vm.MinNumbers);
        Assert.Equal(3, vm.MinSpecial);
        Assert.True(vm.GeneratedPassword.Count(c => "!@#$%^&*".Contains(c)) >= 3);
    }

    [Fact]
    public void HugeMinimums_AreClampedBeforeGeneration()
    {
        var vm = new GeneratorViewModel
        {
            Length = 5,
            IncludeUppercase = true,
            IncludeLowercase = true,
            IncludeNumbers = true,
            IncludeSpecial = true,
            MinNumbers = int.MaxValue,
            MinSpecial = int.MaxValue,
        };

        vm.RegenerateCommand.Execute(null);

        Assert.Equal(5, vm.GeneratedPassword.Length);
        Assert.Equal(0, vm.MinNumbers);
        Assert.Equal(3, vm.MinSpecial);
        Assert.Equal(vm.MinNumbers, vm.MinNumbersValue);
        Assert.Equal(vm.MinSpecial, vm.MinSpecialValue);
    }

    private sealed class UnavailableClipboard : IClipboardService
    {
        public void SetText(string text) => throw new InvalidOperationException("Clipboard unavailable.");
    }

    [Fact]
    public void ModeSwitch_RestoresEachModesUnchangedValue()
    {
        var vm = new GeneratorViewModel();
        var password = vm.GeneratedPassword;
        vm.Mode = GeneratorMode.Passphrase;
        var passphrase = vm.GeneratedPassword;
        vm.Mode = GeneratorMode.Username;
        var username = vm.GeneratedPassword;

        vm.Mode = GeneratorMode.Password;
        Assert.Equal(password, vm.GeneratedPassword);
        vm.Mode = GeneratorMode.Passphrase;
        Assert.Equal(passphrase, vm.GeneratedPassword);
        vm.Mode = GeneratorMode.Username;
        Assert.Equal(username, vm.GeneratedPassword);
    }

    [Fact]
    public void ReopeningWorkspace_PreservesCurrentValueAndOptions()
    {
        var vm = new GeneratorViewModel { Mode = GeneratorMode.Passphrase, PassphraseWordCount = 9 };
        var generated = vm.GeneratedPassword;

        vm.PrepareForDisplay();

        Assert.Equal(GeneratorMode.Passphrase, vm.Mode);
        Assert.Equal(9, vm.PassphraseWordCount);
        Assert.Equal(generated, vm.GeneratedPassword);
    }

    [Fact]
    public void ModeSwitch_DoesNotRestoreValueWithOutdatedOptions()
    {
        var vm = new GeneratorViewModel();
        vm.Mode = GeneratorMode.Passphrase;
        vm.Length = 28;

        vm.Mode = GeneratorMode.Password;

        Assert.Equal(28, vm.GeneratedPassword.Length);
    }

    [Fact]
    public void ModeSelectorTemporaryDeselection_DoesNotResetCurrentMode()
    {
        var vm = new GeneratorViewModel { Mode = GeneratorMode.Passphrase };
        var generated = vm.GeneratedPassword;

        vm.ModeIndex = -1;

        Assert.Equal(GeneratorMode.Passphrase, vm.Mode);
        Assert.Equal(generated, vm.GeneratedPassword);
    }

    [Fact]
    public void ClipboardFailure_DoesNotRecordOrReportSuccessfulCopy()
    {
        var vm = new GeneratorViewModel(new UnavailableClipboard());

        var copied = vm.TryCopyCurrentValue();

        Assert.False(copied);
        Assert.True(vm.CopyFailed);
        Assert.True(vm.HasCopyFeedback);
        Assert.Empty(vm.History);
    }

    [Fact]
    public void MissingClipboard_DoesNotRecordHistory()
    {
        var vm = new GeneratorViewModel();

        Assert.False(vm.TryCopyCurrentValue());
        Assert.Empty(vm.History);
    }

    [Fact]
    public void SuccessfulCopy_RecordsCredentialTypeAndSignalsHistoryAvailability()
    {
        var vm = new GeneratorViewModel(new RecordingClipboard()) { Mode = GeneratorMode.Passphrase };
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        Assert.True(vm.TryCopyCurrentValue());

        Assert.False(vm.CopyFailed);
        Assert.True(vm.HasHistory);
        Assert.False(vm.IsHistoryEmpty);
        Assert.Equal(GeneratorMode.Passphrase, vm.History[0].Mode);
        Assert.Contains(nameof(vm.HasHistory), notifications);
    }

    [Fact]
    public void ClearSensitive_PreventsStaleHistoryCopyAndCachedValueRestoration()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard);
        vm.CopyCommand.Execute(null);
        var staleHistory = vm.History[0];
        vm.Mode = GeneratorMode.Passphrase;
        vm.ClearSensitive();

        vm.Mode = GeneratorMode.Password;
        vm.RegenerateCommand.Execute(null);
        vm.CopyHistoryItemCommand.Execute(staleHistory);

        Assert.False(vm.CanCopy);
        Assert.Equal(string.Empty, vm.GeneratedPassword);
        Assert.Empty(vm.History);
        Assert.Equal(1, clipboard.SecretCount);
        Assert.False(vm.HasCopyFeedback);
    }

    [Fact]
    public void ReopeningAfterUnlock_DoesNotRestorePrelockCachedValue()
    {
        var vm = new GeneratorViewModel { Length = 64 };
        var previous = vm.GeneratedPassword;
        vm.ClearSensitive();

        vm.PrepareForDisplay();

        Assert.True(vm.CanGenerate);
        Assert.Equal(64, vm.GeneratedPassword.Length);
        Assert.NotEqual(previous, vm.GeneratedPassword);
        Assert.Empty(vm.History);
    }

    [Fact]
    public void Copy_RechecksPolicyAndRequiresReviewBeforeCopyingChangedResult()
    {
        var session = CreatePolicySession();
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard, vault: session);
        SetPolicySnapshot(session, Policy("updated", "org-a", """{"minLength":40}"""));

        Assert.False(vm.TryCopyCurrentValue());

        Assert.Equal(40, vm.GeneratedPassword.Length);
        Assert.Empty(vm.History);
        Assert.Equal(0, clipboard.SecretCount);
        Assert.True(vm.TryCopyCurrentValue());
        Assert.Equal(40, clipboard.Text!.Length);
    }

    [Fact]
    public void ReducingLength_NormalizesMinimumsAndRaisesBoundValueNotifications()
    {
        var vm = new GeneratorViewModel
        {
            IncludeSpecial = true,
            Length = 20,
            MinNumbers = 8,
            MinSpecial = 8,
        };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.Length = 5;

        Assert.Equal(3, vm.MinNumbers);
        Assert.Equal(0, vm.MinSpecial);
        Assert.Contains(nameof(vm.MinNumbersValue), changed);
        Assert.Contains(nameof(vm.MinSpecialValue), changed);
        Assert.Equal(5, vm.GeneratedPassword.Length);
    }

    [Fact]
    public void DisablingCharacterSet_ResetsItsMinimum()
    {
        var vm = new GeneratorViewModel { MinNumbers = 4 };

        vm.IncludeNumbers = false;
        vm.MinNumbers = 12;

        Assert.Equal(0, vm.MinNumbers);
        Assert.Equal(0, vm.MinNumbersValue);
        Assert.DoesNotContain(vm.GeneratedPassword, char.IsDigit);
    }

    [Theory]
    [InlineData(0, UsernameGenerationType.RandomWord)]
    [InlineData(1, UsernameGenerationType.PlusAddressedEmail)]
    [InlineData(2, UsernameGenerationType.CatchAllEmail)]
    public void UsernameTypeIndex_MapsSupportedTypesExplicitly(
        int index,
        UsernameGenerationType expected)
    {
        var vm = new GeneratorViewModel { Mode = GeneratorMode.Username };

        vm.UsernameTypeIndex = index;

        Assert.Equal(expected, vm.UsernameType);
        Assert.Equal(index, vm.UsernameTypeIndex);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void UsernameTypeIndex_InvalidValueFallsBackToRandomWord(int index)
    {
        var vm = new GeneratorViewModel
        {
            Mode = GeneratorMode.Username,
            UsernameType = UsernameGenerationType.PlusAddressedEmail,
        };

        vm.UsernameTypeIndex = index;

        Assert.Equal(UsernameGenerationType.RandomWord, vm.UsernameType);
        Assert.Equal(0, vm.UsernameTypeIndex);
        Assert.True(vm.CanGenerate);
        Assert.DoesNotContain("@", vm.GeneratedPassword);
    }

    [Fact]
    public void ForwardedEmailAlias_IsNormalizedToRandomWord()
    {
        var vm = new GeneratorViewModel
        {
            Mode = GeneratorMode.Username,
            UsernameType = UsernameGenerationType.ForwardedEmailAlias,
        };

        Assert.Equal(UsernameGenerationType.RandomWord, vm.UsernameType);
        Assert.Equal(0, vm.UsernameTypeIndex);
        Assert.True(vm.ShowUsernameWordOptions);
        Assert.DoesNotContain("@", vm.GeneratedPassword);
    }

    [Theory]
    [InlineData(UsernameGenerationType.RandomWord, true, false, false)]
    [InlineData(UsernameGenerationType.PlusAddressedEmail, false, true, false)]
    [InlineData(UsernameGenerationType.CatchAllEmail, false, false, true)]
    public void UsernameType_UpdatesVisibleOptionGroup(
        UsernameGenerationType type,
        bool showWord,
        bool showEmail,
        bool showDomain)
    {
        var vm = new GeneratorViewModel { Mode = GeneratorMode.Username };

        vm.UsernameType = type;

        Assert.Equal(showWord, vm.ShowUsernameWordOptions);
        Assert.Equal(showEmail, vm.ShowUsernameEmail);
        Assert.Equal(showDomain, vm.ShowUsernameDomain);
    }

    [Fact]
    public void RandomWordOptions_RegenerateImmediatelyWithFourDigits()
    {
        var vm = new GeneratorViewModel { Mode = GeneratorMode.Username };

        vm.UsernameCapitalize = true;
        vm.UsernameIncludeNumber = true;

        Assert.True(char.IsUpper(vm.GeneratedPassword[0]));
        Assert.Matches(@"\d{4}$", vm.GeneratedPassword);
    }

    [Fact]
    public void PlusAddressedEmail_InvalidInputDisablesThenValidInputRecovers()
    {
        var vm = new GeneratorViewModel
        {
            Mode = GeneratorMode.Username,
            UsernameType = UsernameGenerationType.PlusAddressedEmail,
        };

        Assert.False(vm.CanGenerate);
        Assert.Equal(string.Empty, vm.GeneratedPassword);

        vm.UsernameEmail = "+invalid@example.com";

        Assert.False(vm.CanGenerate);
        Assert.Equal(string.Empty, vm.GeneratedPassword);

        vm.UsernameEmail = "person@example.com";

        Assert.True(vm.CanGenerate);
        Assert.StartsWith("person+", vm.GeneratedPassword, StringComparison.Ordinal);
        Assert.EndsWith("@example.com", vm.GeneratedPassword, StringComparison.Ordinal);
    }

    [Fact]
    public void CatchAllEmail_InvalidDomainDisablesThenValidDomainRecovers()
    {
        var vm = new GeneratorViewModel
        {
            Mode = GeneratorMode.Username,
            UsernameType = UsernameGenerationType.CatchAllEmail,
        };

        vm.UsernameDomain = "https://example.com";

        Assert.False(vm.CanGenerate);
        Assert.Equal(string.Empty, vm.GeneratedPassword);

        vm.UsernameDomain = "example.com";

        Assert.True(vm.CanGenerate);
        Assert.EndsWith("@example.com", vm.GeneratedPassword, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_UsesCurrentVaultEmailOnlyAsTransientUsernameDefault()
    {
        var session = CreatePolicySession();
        var vm = new GeneratorViewModel(vault: session)
        {
            Mode = GeneratorMode.Username,
            UsernameType = UsernameGenerationType.PlusAddressedEmail,
        };

        Assert.Equal("me@example.com", vm.UsernameEmail);
        Assert.True(vm.CanGenerate);
        Assert.StartsWith("me+", vm.GeneratedPassword, StringComparison.Ordinal);
        Assert.EndsWith("@example.com", vm.GeneratedPassword, StringComparison.Ordinal);
    }

    [Fact]
    public void UsernameTypeChange_RaisesVisibilityAndGenerationNotifications()
    {
        var vm = new GeneratorViewModel { Mode = GeneratorMode.Username };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.UsernameType = UsernameGenerationType.PlusAddressedEmail;

        Assert.Contains(nameof(vm.UsernameTypeIndex), changed);
        Assert.Contains(nameof(vm.ShowUsernameWordOptions), changed);
        Assert.Contains(nameof(vm.ShowUsernameEmail), changed);
        Assert.Contains(nameof(vm.ShowUsernameDomain), changed);
        Assert.Contains(nameof(vm.CanGenerate), changed);
        Assert.Contains(nameof(vm.GeneratedPassword), changed);
    }

    [Fact]
    public void SwitchingToPassphrase_GeneratesPassphrase()
    {
        var vm = new GeneratorViewModel();

        vm.Mode = GeneratorMode.Passphrase;

        Assert.Equal(6, vm.GeneratedPassword.Split('-').Length);
    }

    [Fact]
    public void SwitchingToUsername_GeneratesUsername()
    {
        var vm = new GeneratorViewModel();

        vm.Mode = GeneratorMode.Username;

        Assert.False(string.IsNullOrWhiteSpace(vm.GeneratedPassword));
        Assert.DoesNotContain("@", vm.GeneratedPassword);
    }

    [Fact]
    public void Regenerate_DoesNotAddToHistory()
    {
        var vm = new GeneratorViewModel();

        vm.RegenerateCommand.Execute(null);

        Assert.Empty(vm.History);
    }

    [Fact]
    public void ClearHistoryCommand_RemovesHistory()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard);
        vm.CopyCommand.Execute(null);
        Assert.NotEmpty(vm.History);

        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.History);
    }

    [Fact]
    public void CopyHistoryItemCommand_CopiesSelectedHistoryValue()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard);
        vm.CopyCommand.Execute(null);
        var item = vm.History[0];

        vm.CopyHistoryItemCommand.Execute(item);

        Assert.Equal(item.Value, clipboard.Text);
    }

    [Fact]
    public void OptionChanges_DoNotGrowHistory()
    {
        var vm = new GeneratorViewModel();

        vm.Length = 20;
        vm.IncludeSpecial = true;
        vm.Mode = GeneratorMode.Passphrase;

        Assert.Empty(vm.History);
    }

    [Fact]
    public void CopyCommand_RecordsHistory_AndUsesSecretClipboard()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard);

        vm.CopyCommand.Execute(null);

        Assert.Single(vm.History);
        Assert.Equal(vm.GeneratedPassword, vm.History[0].Value);
        Assert.Equal(1, clipboard.SecretCount);
        Assert.Equal(0, clipboard.PlainCount);
    }

    [Fact]
    public void CopyingSameValueTwice_DoesNotDuplicateHistory()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard);

        vm.CopyCommand.Execute(null);
        vm.CopyCommand.Execute(null);

        Assert.Single(vm.History);
    }

    [Fact]
    public void History_CapsAtTwenty()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard);

        for (int i = 0; i < 25; i++)
        {
            vm.RegenerateCommand.Execute(null);
            vm.CopyCommand.Execute(null);
        }

        Assert.Equal(20, vm.History.Count);
    }

    [Fact]
    public void ClearSensitive_ClearsHistoryGeneratedValueAndUsernameInputs()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard)
        {
            Mode = GeneratorMode.Username,
            UsernameType = UsernameGenerationType.PlusAddressedEmail,
            UsernameEmail = "private@example.com",
            UsernameDomain = "private.example.com",
        };
        vm.CopyCommand.Execute(null);

        vm.ClearSensitive();

        Assert.Empty(vm.History);
        Assert.Equal(string.Empty, vm.GeneratedPassword);
        Assert.Equal(string.Empty, vm.UsernameEmail);
        Assert.Equal(string.Empty, vm.UsernameDomain);
        Assert.False(vm.CanGenerate);
    }

    [Fact]
    public void PassphraseWordCountValue_ClampsToRange_SingleSource()
    {
        var vm = new GeneratorViewModel { Mode = GeneratorMode.Passphrase };

        vm.PassphraseWordCountValue = 99;   // above max
        Assert.Equal(20, vm.PassphraseWordCount);

        vm.PassphraseWordCountValue = 1;    // below min
        Assert.Equal(3, vm.PassphraseWordCount);

        vm.PassphraseWordCountValue = 8;    // in range
        Assert.Equal(8, vm.PassphraseWordCount);
        Assert.Equal(8, vm.GeneratedPassword.Split('-').Length);
    }

    [Fact]
    public void PassphraseWordCount_Change_Regenerates_WithoutSelfReassign()
    {
        var vm = new GeneratorViewModel { Mode = GeneratorMode.Passphrase };

        vm.PassphraseWordCount = 5;

        Assert.Equal(5, vm.PassphraseWordCount);            // no clamp-driven mutation
        Assert.Equal(5, vm.GeneratedPassword.Split('-').Length);
    }

    [Fact]
    public void Constructor_AppliesPasswordPolicyToOptionsAndGeneratedValue()
    {
        var session = CreatePolicySession(
            Policy(
                "policy-a",
                "org-a",
                """{"minLength":24,"useUpper":true,"useLower":true,"minNumbers":3,"minSpecial":2}"""));

        var vm = new GeneratorViewModel(vault: session);

        Assert.Equal(GeneratorMode.Password, vm.Mode);
        Assert.Equal(24, vm.Length);
        Assert.True(vm.IncludeUppercase);
        Assert.True(vm.IncludeLowercase);
        Assert.True(vm.IncludeNumbers);
        Assert.True(vm.IncludeSpecial);
        Assert.Equal(3, vm.MinNumbers);
        Assert.Equal(2, vm.MinSpecial);
        Assert.False(vm.CanToggleUppercase);
        Assert.False(vm.CanToggleLowercase);
        Assert.False(vm.CanToggleNumbers);
        Assert.False(vm.CanToggleSpecial);
        Assert.Equal(24, vm.GeneratedPassword.Length);
        Assert.Contains(vm.GeneratedPassword, char.IsUpper);
        Assert.Contains(vm.GeneratedPassword, char.IsLower);
        Assert.True(vm.GeneratedPassword.Count(char.IsDigit) >= 3);
        Assert.True(vm.GeneratedPassword.Count(c => "!@#$%^&*".Contains(c)) >= 2);
    }

    [Fact]
    public void PasswordPolicy_UserChangesCannotWeakenRequiredOptions()
    {
        var session = CreatePolicySession(
            Policy(
                "policy-a",
                "org-a",
                """{"minLength":18,"useUpper":true,"useNumbers":true,"minNumbers":4,"useSpecial":true,"minSpecial":3}"""));
        var vm = new GeneratorViewModel(vault: session);

        vm.Length = 5;
        vm.IncludeUppercase = false;
        vm.IncludeNumbers = false;
        vm.MinNumbers = 0;
        vm.IncludeSpecial = false;
        vm.MinSpecial = 0;

        Assert.True(vm.Length >= 18);
        Assert.True(vm.IncludeUppercase);
        Assert.True(vm.IncludeNumbers);
        Assert.True(vm.IncludeSpecial);
        Assert.True(vm.MinNumbers >= 4);
        Assert.True(vm.MinSpecial >= 3);
        Assert.True(vm.GeneratedPassword.Count(char.IsDigit) >= 4);
        Assert.True(vm.GeneratedPassword.Count(c => "!@#$%^&*".Contains(c)) >= 3);
    }

    [Fact]
    public void Constructor_PassphraseOverrideAppliesPolicyButKeepsUsernameAvailable()
    {
        var session = CreatePolicySession(
            Policy(
                "policy-a",
                "org-a",
                """{"overridePasswordType":"passphrase","minNumberWords":8,"capitalize":true,"includeNumber":true}"""));

        var vm = new GeneratorViewModel(vault: session);

        Assert.Equal(GeneratorMode.Passphrase, vm.Mode);
        Assert.False(vm.CanUsePasswordMode);
        Assert.True(vm.CanUsePassphraseMode);
        Assert.Equal(8, vm.PassphraseWordCount);
        Assert.True(vm.PassphraseCapitalize);
        Assert.True(vm.PassphraseIncludeNumber);
        Assert.False(vm.CanTogglePassphraseCapitalize);
        Assert.False(vm.CanTogglePassphraseIncludeNumber);
        var words = vm.GeneratedPassword.Split('-');
        Assert.Equal(8, words.Length);
        Assert.All(words, word => Assert.True(char.IsUpper(word[0])));
        Assert.Contains(words[^1], char.IsDigit);

        vm.Mode = GeneratorMode.Username;
        vm.RegenerateCommand.Execute(null);

        Assert.Equal(GeneratorMode.Username, vm.Mode);
        Assert.False(string.IsNullOrWhiteSpace(vm.GeneratedPassword));
        Assert.DoesNotContain('-', vm.GeneratedPassword);
    }

    [Fact]
    public void Regenerate_UsesPolicyChangesMadeAfterViewModelConstruction()
    {
        var session = CreatePolicySession();
        var vm = new GeneratorViewModel(vault: session)
        {
            Length = 8,
            IncludeSpecial = false,
        };

        SetPolicySnapshot(
            session,
            Policy(
                "policy-a",
                "org-a",
                """{"overridePasswordType":"passphrase","minNumberWords":9,"capitalize":true,"includeNumber":true}"""));
        vm.RegenerateCommand.Execute(null);

        Assert.Equal(GeneratorMode.Passphrase, vm.Mode);
        Assert.Equal(9, vm.PassphraseWordCount);
        Assert.True(vm.PassphraseCapitalize);
        Assert.True(vm.PassphraseIncludeNumber);
        Assert.Equal(9, vm.GeneratedPassword.Split('-').Length);
    }

    [Fact]
    public void Regenerate_MalformedPolicyFailsClosedAfterApplyingRecognizedOptions()
    {
        var session = CreatePolicySession(
            Policy(
                "policy-a",
                "org-a",
                """{"minLength":"64","useUpper":true,"minSpecial":4}"""));

        var vm = new GeneratorViewModel(vault: session);

        Assert.True(vm.HasGeneratorPolicyNotice);
        Assert.Contains("生成已禁用", vm.GeneratorPolicyNotice);
        Assert.True(vm.IncludeUppercase);
        Assert.True(vm.IncludeSpecial);
        Assert.Equal(4, vm.MinSpecial);
        Assert.False(vm.CanGenerate);
        Assert.Equal(string.Empty, vm.GeneratedPassword);
    }

    [Fact]
    public void Constructor_ImpossiblePolicyFailsClosedWithoutGeneratingAWeakPassword()
    {
        var session = CreatePolicySession(
            Policy("policy-a", "org-a", """{"minNumbers":128,"minSpecial":128}"""));

        var vm = new GeneratorViewModel(vault: session);

        Assert.True(vm.HasGeneratorPolicyNotice);
        Assert.Contains("生成已禁用", vm.GeneratorPolicyNotice);
        Assert.False(vm.CanGenerate);
        Assert.Equal(string.Empty, vm.GeneratedPassword);
    }

    [Fact]
    public void Constructor_MaximumNumericMinimumDropsOptionalSetsAndStillGenerates()
    {
        var session = CreatePolicySession(
            Policy("policy-a", "org-a", """{"minNumbers":128}"""));

        var vm = new GeneratorViewModel(vault: session);

        Assert.False(vm.IncludeUppercase);
        Assert.False(vm.IncludeLowercase);
        Assert.False(vm.CanToggleUppercase);
        Assert.False(vm.CanToggleLowercase);
        Assert.True(vm.IncludeNumbers);
        Assert.Equal(128, vm.MinNumbers);
        Assert.Equal(128, vm.GeneratedPassword.Length);
        Assert.All(vm.GeneratedPassword, character => Assert.True(char.IsDigit(character)));

        vm.IncludeUppercase = true;
        vm.IncludeLowercase = true;

        Assert.False(vm.IncludeUppercase);
        Assert.False(vm.IncludeLowercase);
        Assert.Equal(128, vm.GeneratedPassword.Length);
    }

    private static VaultSession CreatePolicySession(params VaultOrganizationPolicy[] policies)
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account-a",
            "me@example.com",
            new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray()));
        SetPolicySnapshot(session, policies);
        return session;
    }

    private static void SetPolicySnapshot(
        VaultSession session,
        params VaultOrganizationPolicy[] policies)
    {
        var organizationIds = policies
            .Select(policy => policy.OrganizationId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var snapshot = new DecryptedVault(session.Account, [], [], 0, 0)
        {
            Policies = policies,
            PolicyOrganizations = organizationIds
                .Select(id => new VaultOrganizationPolicyContext(
                    id,
                    Status: 2,
                    Role: 2,
                    Enabled: true,
                    UsePolicies: true,
                    IsProviderUser: false))
                .ToArray(),
        };

        Assert.True(session.SetSnapshot(snapshot));
    }

    private static VaultOrganizationPolicy Policy(
        string id,
        string organizationId,
        string dataJson) =>
        new(
            id,
            organizationId,
            (int)OrganizationPolicyType.PasswordGenerator,
            true,
            dataJson);

    private static PasswordStrengthEvaluator CreateEvaluator() =>
        new(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())));

    [Fact]
    public void PasswordMode_WithEvaluator_ExposesStrength()
    {
        var vm = new GeneratorViewModel(clipboard: null, strength: CreateEvaluator());

        Assert.True(vm.ShowStrength);
        Assert.InRange(vm.StrengthScore, 0, 4);
        Assert.False(string.IsNullOrEmpty(vm.StrengthLabel));
        Assert.InRange(vm.StrengthFraction, 0.0 + double.Epsilon, 1.0);

        vm.Mode = GeneratorMode.Username;

        Assert.False(vm.ShowStrength);
    }

    [Fact]
    public void WithoutEvaluator_ShowStrengthReflectsMode_ButScoreStaysZero()
    {
        var vm = new GeneratorViewModel();

        Assert.True(vm.ShowStrength);
        Assert.Equal(0, vm.StrengthScore);

        vm.Mode = GeneratorMode.Username;

        Assert.False(vm.ShowStrength);
        Assert.Equal(0, vm.StrengthScore);
    }
}
