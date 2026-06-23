using System.ComponentModel;
using App.ViewModels;
using App.ViewModels.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace App.Views;

public sealed partial class VaultPage : Page
{
    public VaultViewModel ViewModel { get; }

    public VaultPage()
    {
        // 用完全限定名避免与命名空间 App.Services 冲突(App 类的静态属性 Services)。
        ViewModel = global::App.App.Services.GetRequiredService<VaultViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateDetailTemplate();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string tag)
            ViewModel.SelectFilterByTag(tag);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VaultViewModel.Detail))
            UpdateDetailTemplate();
    }

    // 按 Detail 运行时类型从 Page.Resources 取对应 DataTemplate。
    private void UpdateDetailTemplate()
    {
        var detail = ViewModel.Detail;
        if (detail is null)
        {
            DetailHost.Content = null;
            DetailHost.ContentTemplate = null;
            return;
        }

        var key = detail switch
        {
            LoginDetail => "LoginTemplate",
            CardDetail => "CardTemplate",
            IdentityDetail => "IdentityTemplate",
            NoteDetail => "NoteTemplate",
            SshDetail => "SshTemplate",
            _ => null,
        };

        DetailHost.ContentTemplate = key is not null ? Resources[key] as DataTemplate : null;
        DetailHost.Content = detail;
    }
}
