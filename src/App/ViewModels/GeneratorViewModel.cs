using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.PasswordStrength;
using Vault;

namespace App.ViewModels;

public enum GeneratorMode
{
    Password,
    Passphrase,
    Username,
}

public partial class GeneratorViewModel : ObservableObject, ISensitiveState
{
    private readonly IClipboardService? _clipboard;
    private readonly PasswordStrengthEvaluator? _strength;
    private readonly VaultSession? _vault;

    private GeneratorMode _mode;
    private int _length = 14;
    private bool _includeUppercase = true;
    private bool _includeLowercase = true;
    private bool _includeNumbers = true;
    private bool _includeSpecial;
    private int _minNumbers = 1;
    private int _minSpecial;
    private bool _avoidAmbiguous;
    private string _generatedPassword = string.Empty;
    private int _strengthScore;
    private int _passphraseWordCount = 6;
    private bool _passphraseCapitalize;
    private bool _passphraseIncludeNumber;
    private PasswordGeneratorPolicyRequirements _policy =
        PasswordGeneratorPolicyRequirements.None;
    private bool _refreshingPolicy;

    public ObservableCollection<GeneratorHistoryItem> History { get; } = new();

    public GeneratorMode Mode
    {
        get => _mode;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            var normalized = NormalizeMode(value);
            var changed = SetProperty(ref _mode, normalized);
            if (changed)
            {
                OnPropertyChanged(nameof(ShowStrength));
                OnPropertyChanged(nameof(CanGenerate));
            }
            if (changed || policyChanged)
                Regenerate();
        }
    }

    public int PassphraseWordCount
    {
        get => _passphraseWordCount;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            var clamped = Math.Clamp(value, MinimumPassphraseWordCount, 20);
            var changed = SetProperty(ref _passphraseWordCount, clamped);
            if (changed)
                OnPropertyChanged(nameof(PassphraseWordCountValue));
            if (changed || policyChanged)
                Regenerate();
        }
    }

    [ObservableProperty]
    public partial string PassphraseSeparator { get; set; } = "-";

    public bool PassphraseCapitalize
    {
        get => _passphraseCapitalize;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            var changed = SetProperty(
                ref _passphraseCapitalize,
                value || _policy.Capitalize);
            if (changed || policyChanged)
                Regenerate();
        }
    }

    public bool PassphraseIncludeNumber
    {
        get => _passphraseIncludeNumber;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            var changed = SetProperty(
                ref _passphraseIncludeNumber,
                value || _policy.IncludeNumber);
            if (changed || policyChanged)
                Regenerate();
        }
    }

    private UsernameGenerationType _usernameType = UsernameGenerationType.RandomWord;
    private string _usernameEmail = string.Empty;
    private string _usernameDomain = string.Empty;

    [ObservableProperty]
    public partial bool UsernameCapitalize { get; set; }

    [ObservableProperty]
    public partial bool UsernameIncludeNumber { get; set; }

    public UsernameGenerationType UsernameType
    {
        get => _usernameType;
        set
        {
            var normalized = NormalizeUsernameType(value);
            if (!SetProperty(ref _usernameType, normalized))
                return;

            OnPropertyChanged(nameof(UsernameTypeIndex));
            OnPropertyChanged(nameof(ShowUsernameWordOptions));
            OnPropertyChanged(nameof(ShowUsernameEmail));
            OnPropertyChanged(nameof(ShowUsernameDomain));
            OnPropertyChanged(nameof(CanGenerate));
            Regenerate();
        }
    }

    public string UsernameEmail
    {
        get => _usernameEmail;
        set
        {
            if (!SetProperty(ref _usernameEmail, value ?? string.Empty))
                return;

            OnPropertyChanged(nameof(CanGenerate));
            Regenerate();
        }
    }

    public string UsernameDomain
    {
        get => _usernameDomain;
        set
        {
            if (!SetProperty(ref _usernameDomain, value ?? string.Empty))
                return;

            OnPropertyChanged(nameof(CanGenerate));
            Regenerate();
        }
    }

    public bool ShowUsernameWordOptions => UsernameType == UsernameGenerationType.RandomWord;

    public bool ShowUsernameEmail => UsernameType == UsernameGenerationType.PlusAddressedEmail;

    public bool ShowUsernameDomain => UsernameType == UsernameGenerationType.CatchAllEmail;

    public int Length
    {
        get => _length;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            var clamped = Math.Clamp(value, MinimumPasswordLength, 128);
            var changed = SetProperty(ref _length, clamped);
            if (changed)
            {
                changed |= ApplyNormalizedMinimums(
                    _minNumbers,
                    _minSpecial,
                    MinimumPriority.Numbers);
                OnPropertyChanged(nameof(LengthValue));
            }
            if (changed || policyChanged)
                Regenerate();
        }
    }

    public bool IncludeUppercase
    {
        get => _includeUppercase;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            var normalized = _policy.UseUppercase
                || (value && CanEnableUppercase());
            if (SetProperty(ref _includeUppercase, normalized))
                CharacterSetChanged();
            else if (policyChanged)
                Regenerate();
        }
    }

    public bool IncludeLowercase
    {
        get => _includeLowercase;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            var normalized = _policy.UseLowercase
                || (value && CanEnableLowercase());
            if (SetProperty(ref _includeLowercase, normalized))
                CharacterSetChanged();
            else if (policyChanged)
                Regenerate();
        }
    }

    public bool IncludeNumbers
    {
        get => _includeNumbers;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            if (SetProperty(ref _includeNumbers, value || PolicyRequiresNumbers))
                CharacterSetChanged();
            else if (policyChanged)
                Regenerate();
        }
    }

    public bool IncludeSpecial
    {
        get => _includeSpecial;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            if (SetProperty(ref _includeSpecial, value || PolicyRequiresSpecial))
                CharacterSetChanged();
            else if (policyChanged)
                Regenerate();
        }
    }

    public int MinNumbers
    {
        get => _minNumbers;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            if (ApplyNormalizedMinimums(value, _minSpecial, MinimumPriority.Numbers)
                || policyChanged)
            {
                Regenerate();
            }
        }
    }

    public int MinSpecial
    {
        get => _minSpecial;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            if (ApplyNormalizedMinimums(_minNumbers, value, MinimumPriority.Special)
                || policyChanged)
            {
                Regenerate();
            }
        }
    }

    public bool AvoidAmbiguous
    {
        get => _avoidAmbiguous;
        set
        {
            var policyChanged = RefreshAndApplyPolicy();
            if (SetProperty(ref _avoidAmbiguous, value) || policyChanged)
                Regenerate();
        }
    }

    public string GeneratedPassword
    {
        get => _generatedPassword;
        private set => SetProperty(ref _generatedPassword, value);
    }

    public int StrengthScore => _strengthScore;

    public bool ShowStrength => Mode != GeneratorMode.Username;

    public double StrengthFraction => (_strengthScore + 1) / 5.0;

    public string StrengthLabel => _strengthScore switch
    {
        0 => "很弱",
        1 => "弱",
        2 => "中",
        3 => "强",
        _ => "很强",
    };

    public bool HasGeneratorPolicyNotice =>
        _policy.InEffect || _policy.HasMalformedPolicyData;

    public string GeneratorPolicyNotice => _policy.HasMalformedPolicyData
        ? "组织生成器策略数据无效；密码和密码短语生成已禁用。"
        : "已应用组织生成器策略。";

    public bool CanGenerate => Mode == GeneratorMode.Username
        ? CanGenerateUsername()
        : _policy.CanGenerateCompliantCredential;

    public bool CanUsePasswordMode =>
        _policy.OverridePasswordType != PasswordGeneratorCredentialType.Passphrase;

    public bool CanUsePassphraseMode =>
        _policy.OverridePasswordType != PasswordGeneratorCredentialType.Password;

    public bool CanToggleUppercase =>
        !_policy.UseUppercase && (_includeUppercase || CanEnableUppercase());

    public bool CanToggleLowercase =>
        !_policy.UseLowercase && (_includeLowercase || CanEnableLowercase());

    public bool CanToggleNumbers => !PolicyRequiresNumbers;

    public bool CanToggleSpecial => !PolicyRequiresSpecial;

    public bool CanTogglePassphraseCapitalize => !_policy.Capitalize;

    public bool CanTogglePassphraseIncludeNumber => !_policy.IncludeNumber;

    private bool PolicyRequiresNumbers =>
        _policy.UseNumbers || _policy.MinNumbers > 0;

    private bool PolicyRequiresSpecial =>
        _policy.UseSpecial || _policy.MinSpecial > 0;

    public int PolicyMinimumNumbers => _policy.EffectiveMinNumbers;

    public int PolicyMinimumSpecial => _policy.EffectiveMinSpecial;

    public int MinimumPasswordLength
    {
        get
        {
            var requiredCharacters =
                (_includeUppercase ? 1 : 0)
                + (_includeLowercase ? 1 : 0)
                + PolicyMinimumNumbers
                + PolicyMinimumSpecial;
            return Math.Clamp(
                Math.Max(5, Math.Max(_policy.MinLength, requiredCharacters)),
                5,
                128);
        }
    }

    public int MinimumPassphraseWordCount => Math.Clamp(
        Math.Max(3, _policy.MinNumberWords),
        3,
        20);

    public double LengthValue
    {
        get => Length;
        set => Length = ClampRounded(value, MinimumPasswordLength, 128);
    }

    public double MinNumbersValue
    {
        get => MinNumbers;
        set => MinNumbers = ClampRounded(value, PolicyMinimumNumbers, 128);
    }

    public double MinSpecialValue
    {
        get => MinSpecial;
        set => MinSpecial = ClampRounded(value, PolicyMinimumSpecial, 128);
    }

    public double PassphraseWordCountValue
    {
        get => PassphraseWordCount;
        set => PassphraseWordCount = ClampRounded(value, MinimumPassphraseWordCount, 20);
    }

    public int UsernameTypeIndex
    {
        get => UsernameType switch
        {
            UsernameGenerationType.PlusAddressedEmail => 1,
            UsernameGenerationType.CatchAllEmail => 2,
            _ => 0,
        };
        set => UsernameType = value switch
        {
            1 => UsernameGenerationType.PlusAddressedEmail,
            2 => UsernameGenerationType.CatchAllEmail,
            _ => UsernameGenerationType.RandomWord,
        };
    }

    public GeneratorViewModel(
        IClipboardService? clipboard = null,
        PasswordStrengthEvaluator? strength = null,
        VaultSession? vault = null)
    {
        _clipboard = clipboard;
        _strength = strength;
        _vault = vault;
        _usernameEmail = DefaultUsernameEmail(vault);
        Regenerate();
    }

    private void CharacterSetChanged()
    {
        EnsureAtLeastOneCharacterSet();
        var minimumLength = MinimumPasswordLength;
        if (_length < minimumLength
            && SetProperty(ref _length, minimumLength, nameof(Length)))
        {
            OnPropertyChanged(nameof(LengthValue));
        }
        ApplyNormalizedMinimums(_minNumbers, _minSpecial, MinimumPriority.Numbers);
        OnPropertyChanged(nameof(MinimumPasswordLength));
        OnPropertyChanged(nameof(CanToggleUppercase));
        OnPropertyChanged(nameof(CanToggleLowercase));
        Regenerate();
    }

    private bool RefreshAndApplyPolicy()
    {
        if (_refreshingPolicy)
            return false;

        _refreshingPolicy = true;
        try
        {
            var changed = false;
            var policyChanged = false;
            var next = _vault is null
                ? PasswordGeneratorPolicyRequirements.None
                : PasswordGeneratorPolicyResolver.Resolve(_vault);
            if (next != _policy)
            {
                _policy = next;
                changed = true;
                policyChanged = true;
            }

            var normalizedMode = NormalizeMode(_mode);
            if (SetProperty(ref _mode, normalizedMode, nameof(Mode)))
            {
                changed = true;
                OnPropertyChanged(nameof(ShowStrength));
                OnPropertyChanged(nameof(CanGenerate));
            }

            changed |= RequireOption(
                _policy.UseUppercase,
                ref _includeUppercase,
                nameof(IncludeUppercase));
            changed |= RequireOption(
                _policy.UseLowercase,
                ref _includeLowercase,
                nameof(IncludeLowercase));
            changed |= RequireOption(
                PolicyRequiresNumbers,
                ref _includeNumbers,
                nameof(IncludeNumbers));
            changed |= RequireOption(
                PolicyRequiresSpecial,
                ref _includeSpecial,
                nameof(IncludeSpecial));
            changed |= RequireOption(
                _policy.Capitalize,
                ref _passphraseCapitalize,
                nameof(PassphraseCapitalize));
            changed |= RequireOption(
                _policy.IncludeNumber,
                ref _passphraseIncludeNumber,
                nameof(PassphraseIncludeNumber));
            changed |= DisableOptionalCharacterSetsToFitPolicy();

            var minimumLength = MinimumPasswordLength;
            if (_length < minimumLength
                && SetProperty(ref _length, minimumLength, nameof(Length)))
            {
                changed = true;
                OnPropertyChanged(nameof(LengthValue));
            }

            var minimumWords = MinimumPassphraseWordCount;
            if (_passphraseWordCount < minimumWords
                && SetProperty(
                    ref _passphraseWordCount,
                    minimumWords,
                    nameof(PassphraseWordCount)))
            {
                changed = true;
                OnPropertyChanged(nameof(PassphraseWordCountValue));
            }

            changed |= ApplyNormalizedMinimums(
                Math.Max(_minNumbers, PolicyMinimumNumbers),
                Math.Max(_minSpecial, PolicyMinimumSpecial),
                MinimumPriority.Numbers);
            if (policyChanged)
                NotifyPolicyPropertiesChanged();
            return changed;
        }
        finally
        {
            _refreshingPolicy = false;
        }
    }

    private void NotifyPolicyPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasGeneratorPolicyNotice));
        OnPropertyChanged(nameof(GeneratorPolicyNotice));
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(CanUsePasswordMode));
        OnPropertyChanged(nameof(CanUsePassphraseMode));
        OnPropertyChanged(nameof(CanToggleUppercase));
        OnPropertyChanged(nameof(CanToggleLowercase));
        OnPropertyChanged(nameof(CanToggleNumbers));
        OnPropertyChanged(nameof(CanToggleSpecial));
        OnPropertyChanged(nameof(CanTogglePassphraseCapitalize));
        OnPropertyChanged(nameof(CanTogglePassphraseIncludeNumber));
        OnPropertyChanged(nameof(PolicyMinimumNumbers));
        OnPropertyChanged(nameof(PolicyMinimumSpecial));
        OnPropertyChanged(nameof(MinimumPasswordLength));
        OnPropertyChanged(nameof(MinimumPassphraseWordCount));
    }

    private GeneratorMode NormalizeMode(GeneratorMode mode)
    {
        if (mode == GeneratorMode.Username)
            return mode;

        return _policy.OverridePasswordType switch
        {
            PasswordGeneratorCredentialType.Password => GeneratorMode.Password,
            PasswordGeneratorCredentialType.Passphrase => GeneratorMode.Passphrase,
            _ => mode,
        };
    }

    private bool RequireOption(bool required, ref bool field, string propertyName) =>
        required && SetProperty(ref field, true, propertyName);

    private bool DisableOptionalCharacterSetsToFitPolicy()
    {
        var requiredCharacters = _policy.MinimumRequiredPasswordLength;
        if (requiredCharacters > 128)
            return false;

        var optionalSlots = 128 - requiredCharacters;
        var optionalCount =
            (_includeUppercase && !_policy.UseUppercase ? 1 : 0)
            + (_includeLowercase && !_policy.UseLowercase ? 1 : 0);
        var toDisable = Math.Max(0, optionalCount - optionalSlots);
        var changed = false;
        if (toDisable > 0 && _includeUppercase && !_policy.UseUppercase)
        {
            changed |= SetProperty(
                ref _includeUppercase,
                false,
                nameof(IncludeUppercase));
            toDisable--;
        }

        if (toDisable > 0 && _includeLowercase && !_policy.UseLowercase)
        {
            changed |= SetProperty(
                ref _includeLowercase,
                false,
                nameof(IncludeLowercase));
        }

        return changed;
    }

    private bool CanEnableUppercase()
    {
        var otherOptionalMinimum =
            _includeLowercase && !_policy.UseLowercase ? 1 : 0;
        return _policy.MinimumRequiredPasswordLength + otherOptionalMinimum < 128;
    }

    private bool CanEnableLowercase()
    {
        var otherOptionalMinimum =
            _includeUppercase && !_policy.UseUppercase ? 1 : 0;
        return _policy.MinimumRequiredPasswordLength + otherOptionalMinimum < 128;
    }

    private const int MaxHistory = 20;

    [RelayCommand]
    private void Regenerate()
    {
        RefreshAndApplyPolicy();
        if (!CanGenerate)
        {
            GeneratedPassword = string.Empty;
            UpdateStrength();
            return;
        }

        GeneratedPassword = Mode switch
        {
            GeneratorMode.Passphrase => PasswordGenerator.GeneratePassphrase(new PassphraseGenerationOptions(
                PassphraseWordCount,
                string.IsNullOrEmpty(PassphraseSeparator) ? "-" : PassphraseSeparator,
                PassphraseCapitalize,
                PassphraseIncludeNumber)),
            GeneratorMode.Username => GenerateUsername(),
            _ => PasswordGenerator.Generate(BuildOptions()),
        };
        UpdateStrength();
    }

    private void UpdateStrength()
    {
        var score = (_strength is not null && Mode != GeneratorMode.Username && !string.IsNullOrEmpty(GeneratedPassword))
            ? _strength.Evaluate(GeneratedPassword).Score : 0;
        if (score != _strengthScore)
        {
            _strengthScore = score;
            OnPropertyChanged(nameof(StrengthScore));
            OnPropertyChanged(nameof(StrengthFraction));
            OnPropertyChanged(nameof(StrengthLabel));
        }
    }

    [RelayCommand]
    private void Copy()
    {
        if (string.IsNullOrWhiteSpace(GeneratedPassword))
            return;
        _clipboard?.SetSecretText(GeneratedPassword);
        RecordHistory(GeneratedPassword);
    }

    [RelayCommand]
    private void ClearHistory() => History.Clear();

    [RelayCommand]
    private void CopyHistoryItem(GeneratorHistoryItem? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Value))
            _clipboard?.SetSecretText(item.Value);
    }

    // Record only values the user explicitly copies, deduplicate adjacent entries, and cap at 20.
    private void RecordHistory(string value)
    {
        if (History.Count > 0 && History[0].Value == value)
            return;
        History.Insert(0, new GeneratorHistoryItem(value, DateTimeOffset.Now));
        while (History.Count > MaxHistory)
            History.RemoveAt(History.Count - 1);
    }

    // Clear generated secrets and username inputs when the vault locks or the account signs out.
    public void ClearSensitive()
    {
        History.Clear();
        SetProperty(ref _usernameEmail, string.Empty, nameof(UsernameEmail));
        SetProperty(ref _usernameDomain, string.Empty, nameof(UsernameDomain));
        OnPropertyChanged(nameof(CanGenerate));
        GeneratedPassword = string.Empty;
    }

    partial void OnPassphraseSeparatorChanged(string value) => Regenerate();
    partial void OnUsernameCapitalizeChanged(bool value) => Regenerate();
    partial void OnUsernameIncludeNumberChanged(bool value) => Regenerate();

    private string GenerateUsername()
    {
        try
        {
            return PasswordGenerator.GenerateUsername(new UsernameGenerationOptions(
                UsernameType,
                UsernameCapitalize,
                UsernameIncludeNumber,
                Email: ShowUsernameEmail ? UsernameEmail.Trim() : null,
                Domain: ShowUsernameDomain ? UsernameDomain.Trim() : null));
        }
        catch (Exception exception) when (exception is
            ArgumentException or FormatException or NotSupportedException)
        {
            return string.Empty;
        }
    }

    private bool CanGenerateUsername() => UsernameType switch
    {
        UsernameGenerationType.RandomWord => true,
        UsernameGenerationType.PlusAddressedEmail => IsValidEmail(UsernameEmail),
        UsernameGenerationType.CatchAllEmail => IsValidDomain(UsernameDomain),
        _ => false,
    };

    private static UsernameGenerationType NormalizeUsernameType(UsernameGenerationType type) => type switch
    {
        UsernameGenerationType.RandomWord => type,
        UsernameGenerationType.PlusAddressedEmail => type,
        UsernameGenerationType.CatchAllEmail => type,
        _ => UsernameGenerationType.RandomWord,
    };

    private static string DefaultUsernameEmail(VaultSession? vault)
    {
        var email = vault?.Account.Email?.Trim() ?? string.Empty;
        return IsValidEmail(email) ? email : string.Empty;
    }

    private static bool IsValidEmail(string value)
    {
        var candidate = value.Trim();
        var separator = candidate.IndexOf('@');
        if (separator <= 0
            || separator != candidate.LastIndexOf('@')
            || separator == candidate.Length - 1
            || ContainsWhitespaceOrControl(candidate))
        {
            return false;
        }

        var localPart = candidate[..separator];
        return localPart[0] != '+'
            && localPart[^1] != '+'
            && IsValidDomain(candidate[(separator + 1)..]);
    }

    private static bool IsValidDomain(string value)
    {
        var candidate = value.Trim();
        return candidate.Length is > 0 and <= 253
            && !candidate.StartsWith(".", StringComparison.Ordinal)
            && !candidate.EndsWith(".", StringComparison.Ordinal)
            && candidate.IndexOfAny(['@', '/', '\\', ':']) < 0
            && !ContainsWhitespaceOrControl(candidate)
            && Uri.CheckHostName(candidate) == UriHostNameType.Dns;
    }

    private static bool ContainsWhitespaceOrControl(string value) =>
        value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    private PasswordGenerationOptions BuildOptions()
    {
        EnsureAtLeastOneCharacterSet();
        ApplyNormalizedMinimums(_minNumbers, _minSpecial, MinimumPriority.Numbers);

        var minUppercase = IncludeUppercase ? 1 : 0;
        var minLowercase = IncludeLowercase ? 1 : 0;

        return new PasswordGenerationOptions(
            Length,
            IncludeUppercase,
            IncludeLowercase,
            IncludeNumbers,
            IncludeSpecial,
            minUppercase,
            minLowercase,
            _minNumbers,
            _minSpecial,
            AvoidAmbiguous);
    }

    private bool EnsureAtLeastOneCharacterSet()
    {
        if (!IncludeUppercase && !IncludeLowercase && !IncludeNumbers && !IncludeSpecial)
        {
            SetProperty(ref _includeLowercase, true, nameof(IncludeLowercase));
            return true;
        }

        return false;
    }

    private bool ApplyNormalizedMinimums(
        int requestedNumbers,
        int requestedSpecial,
        MinimumPriority priority)
    {
        var numberFloor = IncludeNumbers ? PolicyMinimumNumbers : 0;
        var specialFloor = IncludeSpecial ? PolicyMinimumSpecial : 0;
        requestedNumbers = IncludeNumbers
            ? Math.Clamp(requestedNumbers, numberFloor, 128)
            : 0;
        requestedSpecial = IncludeSpecial
            ? Math.Clamp(requestedSpecial, specialFloor, 128)
            : 0;

        var fixedMinimums = (IncludeUppercase ? 1 : 0) + (IncludeLowercase ? 1 : 0);
        var remaining = Math.Max(0, Length - fixedMinimums);

        if (remaining < numberFloor + specialFloor)
        {
            var requiredLength = Math.Min(128, fixedMinimums + numberFloor + specialFloor);
            if (SetProperty(ref _length, requiredLength, nameof(Length)))
                OnPropertyChanged(nameof(LengthValue));
            remaining = Math.Max(0, _length - fixedMinimums);
        }

        int normalizedNumbers;
        int normalizedSpecial;
        if (priority == MinimumPriority.Special)
        {
            normalizedSpecial = Math.Clamp(
                requestedSpecial,
                specialFloor,
                Math.Max(specialFloor, remaining - numberFloor));
            normalizedNumbers = Math.Clamp(
                requestedNumbers,
                numberFloor,
                Math.Max(numberFloor, remaining - normalizedSpecial));
        }
        else
        {
            normalizedNumbers = Math.Clamp(
                requestedNumbers,
                numberFloor,
                Math.Max(numberFloor, remaining - specialFloor));
            normalizedSpecial = Math.Clamp(
                requestedSpecial,
                specialFloor,
                Math.Max(specialFloor, remaining - normalizedNumbers));
        }

        var numbersChanged = SetMinimum(
            ref _minNumbers,
            normalizedNumbers,
            nameof(MinNumbers),
            nameof(MinNumbersValue));
        var specialChanged = SetMinimum(
            ref _minSpecial,
            normalizedSpecial,
            nameof(MinSpecial),
            nameof(MinSpecialValue));
        return numbersChanged || specialChanged;
    }

    private bool SetMinimum(ref int field, int value, string propertyName, string valuePropertyName)
    {
        if (!SetProperty(ref field, value, propertyName))
            return false;

        OnPropertyChanged(valuePropertyName);
        return true;
    }

    private enum MinimumPriority
    {
        Numbers,
        Special,
    }

    private static int ClampRounded(double value, int minimum, int maximum)
    {
        if (double.IsNaN(value) || value <= minimum)
            return minimum;

        if (value >= maximum)
            return maximum;

        return Math.Clamp((int)Math.Round(value), minimum, maximum);
    }
}
