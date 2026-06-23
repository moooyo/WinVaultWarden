using App.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

public class GeneratorViewModelTests
{
    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public void SetText(string text) => Text = text;
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
}
