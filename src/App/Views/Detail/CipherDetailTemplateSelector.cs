using App.ViewModels.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views.Detail;

public sealed class CipherDetailTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Login { get; set; }
    public DataTemplate? Card { get; set; }
    public DataTemplate? Identity { get; set; }
    public DataTemplate? Note { get; set; }
    public DataTemplate? Ssh { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        LoginDetail => Login,
        CardDetail => Card,
        IdentityDetail => Identity,
        NoteDetail => Note,
        SshDetail => Ssh,
        _ => base.SelectTemplateCore(item),
    };
}
