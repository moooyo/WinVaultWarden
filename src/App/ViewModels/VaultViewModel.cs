using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Models;
using Core.Services;

namespace App.ViewModels;

public partial class VaultViewModel : ObservableObject
{
    public ObservableCollection<Cipher> Ciphers { get; } = new();

    public VaultViewModel(IVaultService vault)
    {
        foreach (var c in vault.GetCiphers())
            Ciphers.Add(c);
    }
}
