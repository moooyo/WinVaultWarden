using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crypto;
using Crypto.PasswordStrength;

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

    public ObservableCollection<GeneratorHistoryItem> History { get; } = new();

    [ObservableProperty]
    public partial GeneratorMode Mode { get; set; }

    [ObservableProperty]
    public partial int PassphraseWordCount { get; set; } = 6;

    [ObservableProperty]
    public partial string PassphraseSeparator { get; set; } = "-";

    [ObservableProperty]
    public partial bool PassphraseCapitalize { get; set; }

    [ObservableProperty]
    public partial bool PassphraseIncludeNumber { get; set; }

    private UsernameGenerationType _usernameType = UsernameGenerationType.RandomWord;

    [ObservableProperty]
    public partial bool UsernameCapitalize { get; set; }

    [ObservableProperty]
    public partial bool UsernameIncludeNumber { get; set; }

    public UsernameGenerationType UsernameType
    {
        get => _usernameType;
        set
        {
            var normalized = value == UsernameGenerationType.RandomWord
                ? value
                : UsernameGenerationType.RandomWord;
            if (!SetProperty(ref _usernameType, normalized))
                return;

            OnPropertyChanged(nameof(UsernameTypeIndex));
            Regenerate();
        }
    }

    public int Length
    {
        get => _length;
        set
        {
            var clamped = Math.Clamp(value, 5, 128);
            if (!SetProperty(ref _length, clamped))
                return;

            ApplyNormalizedMinimums(_minNumbers, _minSpecial, MinimumPriority.Numbers);
            OnPropertyChanged(nameof(LengthValue));
            Regenerate();
        }
    }

    public bool IncludeUppercase
    {
        get => _includeUppercase;
        set
        {
            if (SetProperty(ref _includeUppercase, value))
                CharacterSetChanged();
        }
    }

    public bool IncludeLowercase
    {
        get => _includeLowercase;
        set
        {
            if (SetProperty(ref _includeLowercase, value))
                CharacterSetChanged();
        }
    }

    public bool IncludeNumbers
    {
        get => _includeNumbers;
        set
        {
            if (SetProperty(ref _includeNumbers, value))
                CharacterSetChanged();
        }
    }

    public bool IncludeSpecial
    {
        get => _includeSpecial;
        set
        {
            if (SetProperty(ref _includeSpecial, value))
                CharacterSetChanged();
        }
    }

    public int MinNumbers
    {
        get => _minNumbers;
        set
        {
            if (ApplyNormalizedMinimums(value, _minSpecial, MinimumPriority.Numbers))
                Regenerate();
        }
    }

    public int MinSpecial
    {
        get => _minSpecial;
        set
        {
            if (ApplyNormalizedMinimums(_minNumbers, value, MinimumPriority.Special))
                Regenerate();
        }
    }

    public bool AvoidAmbiguous
    {
        get => _avoidAmbiguous;
        set
        {
            if (SetProperty(ref _avoidAmbiguous, value))
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

    public double LengthValue
    {
        get => Length;
        set => Length = ClampRounded(value, 5, 128);
    }

    public double MinNumbersValue
    {
        get => MinNumbers;
        set => MinNumbers = ClampRounded(value, 0, 128);
    }

    public double MinSpecialValue
    {
        get => MinSpecial;
        set => MinSpecial = ClampRounded(value, 0, 128);
    }

    public double PassphraseWordCountValue
    {
        get => PassphraseWordCount;
        set => PassphraseWordCount = ClampRounded(value, 3, 20);
    }

    public int UsernameTypeIndex
    {
        get => (int)UsernameType;
        set => UsernameType = UsernameGenerationType.RandomWord;
    }

    public GeneratorViewModel(IClipboardService? clipboard = null, PasswordStrengthEvaluator? strength = null)
    {
        _clipboard = clipboard;
        _strength = strength;
        Regenerate();
    }

    private void CharacterSetChanged()
    {
        EnsureAtLeastOneCharacterSet();
        ApplyNormalizedMinimums(_minNumbers, _minSpecial, MinimumPriority.Numbers);
        Regenerate();
    }

    private const int MaxHistory = 20;

    [RelayCommand]
    private void Regenerate()
    {
        GeneratedPassword = Mode switch
        {
            GeneratorMode.Passphrase => PasswordGenerator.GeneratePassphrase(new PassphraseGenerationOptions(
                PassphraseWordCount,
                string.IsNullOrEmpty(PassphraseSeparator) ? "-" : PassphraseSeparator,
                PassphraseCapitalize,
                PassphraseIncludeNumber)),
            GeneratorMode.Username => PasswordGenerator.GenerateUsername(new UsernameGenerationOptions(
                UsernameType,
                UsernameCapitalize,
                UsernameIncludeNumber)),
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

    // 仅在用户显式复制/采用时记录;相邻去重 + 封顶 20。
    private void RecordHistory(string value)
    {
        if (History.Count > 0 && History[0].Value == value)
            return;
        History.Insert(0, new GeneratorHistoryItem(value, DateTimeOffset.Now));
        while (History.Count > MaxHistory)
            History.RemoveAt(History.Count - 1);
    }

    // 锁屏/登出时清除内存中的明文密钥足迹:历史与当前生成结果。经 DI TrackSensitive 弱登记后由协调器统一调用。
    public void ClearSensitive()
    {
        History.Clear();
        GeneratedPassword = string.Empty;
    }

    partial void OnModeChanged(GeneratorMode value)
    {
        OnPropertyChanged(nameof(ShowStrength));
        Regenerate();
        UpdateStrength();
    }
    partial void OnPassphraseWordCountChanged(int value)
    {
        OnPropertyChanged(nameof(PassphraseWordCountValue));
        Regenerate();
    }
    partial void OnPassphraseSeparatorChanged(string value) => Regenerate();
    partial void OnPassphraseCapitalizeChanged(bool value) => Regenerate();
    partial void OnPassphraseIncludeNumberChanged(bool value) => Regenerate();
    partial void OnUsernameCapitalizeChanged(bool value) => Regenerate();
    partial void OnUsernameIncludeNumberChanged(bool value) => Regenerate();

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
        requestedNumbers = IncludeNumbers ? Math.Clamp(requestedNumbers, 0, 128) : 0;
        requestedSpecial = IncludeSpecial ? Math.Clamp(requestedSpecial, 0, 128) : 0;

        var fixedMinimums = (IncludeUppercase ? 1 : 0) + (IncludeLowercase ? 1 : 0);
        var remaining = Math.Max(0, Length - fixedMinimums);

        int normalizedNumbers;
        int normalizedSpecial;
        if (priority == MinimumPriority.Special)
        {
            normalizedSpecial = Math.Min(requestedSpecial, remaining);
            normalizedNumbers = Math.Min(requestedNumbers, remaining - normalizedSpecial);
        }
        else
        {
            normalizedNumbers = Math.Min(requestedNumbers, remaining);
            normalizedSpecial = Math.Min(requestedSpecial, remaining - normalizedNumbers);
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
