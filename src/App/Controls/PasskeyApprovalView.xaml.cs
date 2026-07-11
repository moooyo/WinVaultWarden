using App.Services;
using Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Controls;

// Account-selection + provenance surface for a passkey assertion. The presentation is built from the
// Task 1 validated context (never page-reported values); this control only renders it and reports the
// user's explicit credential pick. Multiple candidates start with no selection so the dialog's primary
// button stays disabled until the user chooses.
public sealed partial class PasskeyApprovalView : UserControl
{
    public PasskeyApprovalPresentation Presentation { get; }

    public PasskeyCredentialTarget? SelectedCredential { get; set; }

    public event EventHandler? SelectionChanged;

    public PasskeyApprovalView(PasskeyApprovalPresentation presentation)
    {
        Presentation = presentation;
        SelectedCredential = presentation.InitialSelection;
        InitializeComponent();
        // A lone credential is shown inline elsewhere; only surface the picker when there is a choice.
        CredentialPicker.Visibility = presentation.Candidates.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SelectionChanged?.Invoke(this, EventArgs.Empty);
}
