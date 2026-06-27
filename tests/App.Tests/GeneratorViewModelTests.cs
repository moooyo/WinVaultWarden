using App.Services;
using App.ViewModels;
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
        public void SetSecretText(string text, int autoClearSeconds = 30) { Text = text; SecretCount++; }
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
        Assert.Equal(128, vm.MinNumbers);
        Assert.Equal(128, vm.MinSpecial);
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
    public void Regenerate_AddsGeneratedValueToHistory()
    {
        var vm = new GeneratorViewModel();

        vm.RegenerateCommand.Execute(null);

        Assert.NotEmpty(vm.History);
        Assert.Equal(vm.GeneratedPassword, vm.History[0].Value);
    }

    [Fact]
    public void ClearHistoryCommand_RemovesHistory()
    {
        var vm = new GeneratorViewModel();
        vm.RegenerateCommand.Execute(null);

        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.History);
    }

    [Fact]
    public void CopyHistoryItemCommand_CopiesSelectedHistoryValue()
    {
        var clipboard = new RecordingClipboard();
        var vm = new GeneratorViewModel(clipboard);
        vm.RegenerateCommand.Execute(null);
        var item = vm.History[0];

        vm.CopyHistoryItemCommand.Execute(item);

        Assert.Equal(item.Value, clipboard.Text);
    }
}
