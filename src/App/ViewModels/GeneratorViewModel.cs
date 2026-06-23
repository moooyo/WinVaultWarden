using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Crypto;

namespace App.ViewModels;

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
            var normalized = Math.Max(0, value);
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
            var normalized = Math.Max(0, value);
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
        set => Length = Math.Clamp((int)Math.Round(value), 5, 128);
    }

    public double MinNumbersValue
    {
        get => MinNumbers;
        set => MinNumbers = Math.Max(0, (int)Math.Round(value));
    }

    public double MinSpecialValue
    {
        get => MinSpecial;
        set => MinSpecial = Math.Max(0, (int)Math.Round(value));
    }

    public GeneratorViewModel(IClipboardService? clipboard = null)
    {
        _clipboard = clipboard;
        Regenerate();
    }

    private void CharacterSetChanged()
    {
        EnsureAtLeastOneCharacterSet();
        Regenerate();
    }

    [RelayCommand]
    private void Regenerate() => GeneratedPassword = PasswordGenerator.Generate(BuildOptions());

    [RelayCommand]
    private void Copy()
    {
        if (!string.IsNullOrWhiteSpace(GeneratedPassword))
            _clipboard?.SetText(GeneratedPassword);
    }

    private PasswordGenerationOptions BuildOptions()
    {
        EnsureAtLeastOneCharacterSet();

        var minUppercase = IncludeUppercase ? 1 : 0;
        var minLowercase = IncludeLowercase ? 1 : 0;
        var minNumbers = IncludeNumbers ? Math.Max(0, MinNumbers) : 0;
        var minSpecial = IncludeSpecial ? Math.Max(0, MinSpecial) : 0;

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

    private void EnsureAtLeastOneCharacterSet()
    {
        if (!IncludeUppercase && !IncludeLowercase && !IncludeNumbers && !IncludeSpecial)
            IncludeLowercase = true;
    }

    private void ReduceMinimumsToFitLength(ref int minNumbers, ref int minSpecial, int fixedMinimums)
    {
        var overflow = fixedMinimums + minNumbers + minSpecial - Length;
        if (overflow <= 0)
            return;

        var specialReduction = Math.Min(minSpecial, overflow);
        minSpecial -= specialReduction;
        overflow -= specialReduction;

        var numberReduction = Math.Min(minNumbers, overflow);
        minNumbers -= numberReduction;
    }
}
