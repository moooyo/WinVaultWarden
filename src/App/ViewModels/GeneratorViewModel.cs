using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crypto;

namespace App.ViewModels;

public enum GeneratorMode
{
    Password,
    Passphrase,
    Username,
}

public partial class GeneratorViewModel : ObservableObject
{
    private readonly IClipboardService? _clipboard;

    private int _length = 14;
    private bool _includeUppercase = true;
    private bool _includeLowercase = true;
    private bool _includeNumbers = true;
    private bool _includeSpecial;
    private int _minNumbers = 1;
    private int _minSpecial;
    private bool _avoidAmbiguous;
    private string _generatedPassword = string.Empty;

    public ObservableCollection<GeneratorHistoryItem> History { get; } = new();

    [ObservableProperty] private GeneratorMode _mode;
    [ObservableProperty] private int _passphraseWordCount = 6;
    [ObservableProperty] private string _passphraseSeparator = "-";
    [ObservableProperty] private bool _passphraseCapitalize;
    [ObservableProperty] private bool _passphraseIncludeNumber;
    [ObservableProperty] private UsernameGenerationType _usernameType = UsernameGenerationType.RandomWord;
    [ObservableProperty] private bool _usernameCapitalize;
    [ObservableProperty] private bool _usernameIncludeNumber;

    public int Length
    {
        get => _length;
        set
        {
            var clamped = Math.Clamp(value, 5, 128);
            if (!SetProperty(ref _length, clamped))
                return;

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
            var normalized = Math.Clamp(value, 0, 128);
            if (!SetProperty(ref _minNumbers, normalized))
                return;

            OnPropertyChanged(nameof(MinNumbersValue));
            Regenerate();
        }
    }

    public int MinSpecial
    {
        get => _minSpecial;
        set
        {
            var normalized = Math.Clamp(value, 0, 128);
            if (!SetProperty(ref _minSpecial, normalized))
                return;

            OnPropertyChanged(nameof(MinSpecialValue));
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
        set => UsernameType = (UsernameGenerationType)Math.Clamp(value, 0, 3);
    }

    public GeneratorViewModel(IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;
        Regenerate();
    }

    private void CharacterSetChanged()
    {
        var fallbackApplied = EnsureAtLeastOneCharacterSet();
        if (fallbackApplied || IncludeUppercase || IncludeLowercase || IncludeNumbers || IncludeSpecial)
            Regenerate();
    }

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

        History.Insert(0, new GeneratorHistoryItem(GeneratedPassword, DateTimeOffset.Now));
    }

    [RelayCommand]
    private void Copy()
    {
        if (!string.IsNullOrWhiteSpace(GeneratedPassword))
            _clipboard?.SetText(GeneratedPassword);
    }

    [RelayCommand]
    private void ClearHistory() => History.Clear();

    [RelayCommand]
    private void CopyHistoryItem(GeneratorHistoryItem? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Value))
            _clipboard?.SetText(item.Value);
    }

    partial void OnModeChanged(GeneratorMode value) => Regenerate();
    partial void OnPassphraseWordCountChanged(int value)
    {
        PassphraseWordCount = Math.Clamp(value, 3, 20);
        OnPropertyChanged(nameof(PassphraseWordCountValue));
        Regenerate();
    }
    partial void OnPassphraseSeparatorChanged(string value) => Regenerate();
    partial void OnPassphraseCapitalizeChanged(bool value) => Regenerate();
    partial void OnPassphraseIncludeNumberChanged(bool value) => Regenerate();
    partial void OnUsernameTypeChanged(UsernameGenerationType value)
    {
        OnPropertyChanged(nameof(UsernameTypeIndex));
        Regenerate();
    }
    partial void OnUsernameCapitalizeChanged(bool value) => Regenerate();
    partial void OnUsernameIncludeNumberChanged(bool value) => Regenerate();

    private PasswordGenerationOptions BuildOptions()
    {
        EnsureAtLeastOneCharacterSet();

        var minUppercase = IncludeUppercase ? 1 : 0;
        var minLowercase = IncludeLowercase ? 1 : 0;
        var minNumbers = IncludeNumbers ? Math.Clamp(MinNumbers, 0, 128) : 0;
        var minSpecial = IncludeSpecial ? Math.Clamp(MinSpecial, 0, 128) : 0;

        ReduceMinimumsToFitLength(ref minNumbers, ref minSpecial, minUppercase + minLowercase);

        return new PasswordGenerationOptions(
            Length,
            IncludeUppercase,
            IncludeLowercase,
            IncludeNumbers,
            IncludeSpecial,
            minUppercase,
            minLowercase,
            minNumbers,
            minSpecial,
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

    private void ReduceMinimumsToFitLength(ref int minNumbers, ref int minSpecial, int fixedMinimums)
    {
        var overflow = (long)fixedMinimums + minNumbers + minSpecial - Length;
        if (overflow <= 0)
            return;

        var specialReduction = (int)Math.Min(minSpecial, overflow);
        minSpecial -= specialReduction;
        overflow -= specialReduction;

        var numberReduction = (int)Math.Min(minNumbers, overflow);
        minNumbers -= numberReduction;
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
