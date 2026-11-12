using App.ViewModels;
using App.Services;
using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class EmergencyAccessPage : Page
{
    public EmergencyAccessViewModel ViewModel { get; }

    public EmergencyAccessPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<EmergencyAccessViewModel>();
        InitializeComponent();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.LoadAsync();
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = ViewModel.LoadAsync();

    public static Visibility ShowLists(bool hasContent, bool isEmpty) =>
        hasContent || isEmpty ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowDefaultContactIcon(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.RecoveryInitiated ? Visibility.Collapsed : Visibility.Visible;

    public static string FormatContactName(string? name, string? email) =>
        string.IsNullOrWhiteSpace(name) ? email ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text16062E9CA95920EA) : name;

    public static string FormatContactAccess(EmergencyAccessType type) =>
        type == EmergencyAccessType.Takeover ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBAE0C2C1C1CB03AE) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text791E312F7A4A02E7);

    public static string FormatGrantedAccess(EmergencyAccessType type) =>
        type == EmergencyAccessType.Takeover ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFA219171EF733549) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD25C36AB22A362BC);

    public static string FormatContactDetail(string? email, EmergencyAccessStatus status, int waitDays)
    {
        var detail = status switch
        {
            EmergencyAccessStatus.Invited => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5D612381C8543EC8),
            EmergencyAccessStatus.Accepted => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text18157D9EBC560323),
            EmergencyAccessStatus.Confirmed => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text42A52DC62BF89E89,waitDays),
            EmergencyAccessStatus.RecoveryInitiated => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextC858EBB7D4FC2A8E,waitDays),
            EmergencyAccessStatus.RecoveryApproved => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE4733D431FA5A1D8),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEC0D9BDB00A4A8F6),
        };
        return string.IsNullOrWhiteSpace(email) ? detail : $"{email} · {detail}";
    }

    public static string FormatGrantedDetail(string? email, EmergencyAccessStatus status, int waitDays)
    {
        var detail = status switch
        {
            EmergencyAccessStatus.Invited => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1577599B341D61A6),
            EmergencyAccessStatus.Accepted => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4DD7A85ED1C8A5E3),
            EmergencyAccessStatus.RecoveryInitiated => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text6C74025FB12F1739,waitDays),
            EmergencyAccessStatus.RecoveryApproved => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE4733D431FA5A1D8),
            _ => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text42A52DC62BF89E89,waitDays),
        };
        return string.IsNullOrWhiteSpace(email) ? detail : $"{email} · {detail}";
    }

    public static string FormatStatus(EmergencyAccessStatus status) => status switch
    {
        EmergencyAccessStatus.Invited => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF3D08AA6AF05BBD8),
        EmergencyAccessStatus.Accepted => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCCA6F7BCFE680C7F),
        EmergencyAccessStatus.Confirmed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD56A511F498F7E73),
        EmergencyAccessStatus.RecoveryInitiated => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB87F824D61AC1726),
        EmergencyAccessStatus.RecoveryApproved => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text84B381CBFE499AD9),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEC0D9BDB00A4A8F6),
    };

    public static string FormatAccessType(EmergencyAccessType type) =>
        type == EmergencyAccessType.Takeover ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text586389949E1BDCD3) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text678CD532990E3743);

    public static string FormatWaitTime(int days) => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text8351D713514A46AF,days);

    public static string FormatStatusHelp(EmergencyAccessStatus status, EmergencyAccessType type) => status switch
    {
        EmergencyAccessStatus.Invited => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAC53D5FE08546E6B),
        EmergencyAccessStatus.Accepted => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text56A46FFDDB6F52DE),
        EmergencyAccessStatus.RecoveryInitiated => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC73AFF729DC3D7C8),
        EmergencyAccessStatus.RecoveryApproved when type == EmergencyAccessType.Takeover => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBC38F5515324B9D0),
        EmergencyAccessStatus.RecoveryApproved => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4F9E269979C5E5B7),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA0E06C76EE11BA39),
    };

    private static string AccessConsequences(EmergencyAccessType type) => type == EmergencyAccessType.Takeover
        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text196FD22E95189BE3)
        : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text80DAE00BEA500035);

    private static bool IsValidWaitTime(double value) =>
        double.IsFinite(value) && value >= 1 && value <= 90 && value == Math.Truncate(value);

    private async Task<bool> ConfirmEmergencyAsync(
        string title,
        string message,
        string primaryText,
        bool dangerous = false,
        CancellationToken ct = default)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
            DefaultButton = ContentDialogButton.Close,
        };
        if (dangerous)
            dialog.PrimaryButtonStyle = (Style)Resources["EmergencyDangerButtonStyle"];
        return await DialogHelper.ShowTrackedAsync(dialog, ct: ct) == ContentDialogResult.Primary;
    }


    public static Visibility ShowReinvite(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.Invited ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowConfirm(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.Accepted ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowUpdate(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.Confirmed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowApprove(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.RecoveryInitiated ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowReject(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.RecoveryInitiated ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowRemove(EmergencyAccessStatus status) =>
        status is EmergencyAccessStatus.Invited
            or EmergencyAccessStatus.Accepted
            or EmergencyAccessStatus.Confirmed
            or EmergencyAccessStatus.RecoveryInitiated
            or EmergencyAccessStatus.RecoveryApproved
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static Visibility ShowInitiate(EmergencyAccessStatus status) =>
        status == EmergencyAccessStatus.Confirmed ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility ShowView(
        EmergencyAccessStatus status,
        EmergencyAccessType type) =>
        status == EmergencyAccessStatus.RecoveryApproved
            && type == EmergencyAccessType.View
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static Visibility ShowTakeover(
        EmergencyAccessStatus status,
        EmergencyAccessType type) =>
        status == EmergencyAccessStatus.RecoveryApproved
            && type == EmergencyAccessType.Takeover
            ? Visibility.Visible
            : Visibility.Collapsed;


    private async void OnInviteClick(object sender, RoutedEventArgs e)
    {
        var emailBox = new TextBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text342BB33E19E06487),
            PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text342BB33E19E06487),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(emailBox, "InviteEmailBox");

        var typeCombo = new ComboBox { MinWidth = 180, Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text93F0F38887A80DB3) };
        typeCombo.Items.Add(new ComboBoxItem { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDB8DB0530432BD15), Tag = EmergencyAccessType.View });
        typeCombo.Items.Add(new ComboBoxItem { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEA878D5D090510DA), Tag = EmergencyAccessType.Takeover });
        typeCombo.SelectedIndex = 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(typeCombo, "InviteTypeCombo");

        var waitDaysBox = new NumberBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC745136D694620DE),
            Minimum = 1,
            Maximum = 90,
            Value = 7,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(waitDaysBox, "InviteWaitDaysBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(emailBox);
        panel.Children.Add(typeCombo);
        var accessHelp = new TextBlock { Text = AccessConsequences(EmergencyAccessType.View), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(accessHelp);
        panel.Children.Add(waitDaysBox);
        panel.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2D111A2DDDD694B0),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEDAADB10E9ECA3B3),
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3A3BCF41A1AB60FC),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
        };

        void UpdateInviteState()
        {
            errorText.Visibility = Visibility.Collapsed;
            var email = emailBox.Text.Trim();
            dialog.IsPrimaryButtonEnabled = System.Net.Mail.MailAddress.TryCreate(email, out var address)
                && string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)
                && IsValidWaitTime(waitDaysBox.Value);
        }
        emailBox.TextChanged += (_, _) => UpdateInviteState();
        waitDaysBox.ValueChanged += (_, _) => UpdateInviteState();
        typeCombo.SelectionChanged += (_, _) =>
        {
            accessHelp.Text = AccessConsequences(
                typeCombo.SelectedItem is ComboBoxItem { Tag: EmergencyAccessType type } ? type : EmergencyAccessType.View);
            UpdateInviteState();
        };

        while (true)
        {
            var dialogResult = await DialogHelper.ShowTrackedAsync(dialog);
            if (dialogResult != ContentDialogResult.Primary)
                return;

            var email = emailBox.Text.Trim();
            var type = typeCombo.SelectedItem is ComboBoxItem selectedType && selectedType.Tag is EmergencyAccessType t
                ? t
                : EmergencyAccessType.View;
            var waitDays = double.IsNaN(waitDaysBox.Value) ? 7 : (int)waitDaysBox.Value;

            var result = await ViewModel.InviteAsync(email, type, waitDays);
            if (result.IsSuccess)
                return;

            errorText.Text = result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2135C2ACE997D92C);
            errorText.Visibility = Visibility.Visible;
        }
    }


    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        if (!await DialogHelper.ConfirmAsync(XamlRoot, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text0FF23F074A9F88D9,FormatContactName(item.Name, item.Email)),
            $"{item.Email}\n{AccessConsequences(item.Type)}\n{FormatWaitTime(item.WaitTimeDays)}", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text36F33ADAF0942634)))
            return;
        await ViewModel.ConfirmAsync(item);
    }

    private async void OnReinviteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        await ViewModel.ReinviteAsync(item);
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        if (!await ConfirmEmergencyAsync(global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextF555E4F9B97C51F9,FormatContactName(item.Name, item.Email)),
            global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text574920067B4F1675,item.Email,AccessConsequences(item.Type)), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8CBE697B157364A5),
            dangerous: item.Type == EmergencyAccessType.Takeover))
            return;
        await ViewModel.ApproveAsync(item);
    }

    private async void OnRejectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        await ViewModel.RejectAsync(
            item,
            (contact, ct) => DialogHelper.ConfirmAsync(
                XamlRoot,
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text559BCFDF9F61A8BE),
                global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text20950383A18936CD,contact.Email),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text136DE7A8C46FC803),
                ct: ct),
            CancellationToken.None);
    }

    private async void OnRemoveContactClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;
        await ViewModel.RemoveAsync(
            item,
            (contact, ct) => ConfirmEmergencyAsync(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB92C01670A4005B4),
                global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text433F7DB008613D0F,contact.Email),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6135D4159E892541),
                dangerous: true,
                ct: ct),
            CancellationToken.None);
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EmergencyContact item)
            return;

        var typeCombo = new ComboBox { MinWidth = 180, Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text93F0F38887A80DB3) };
        typeCombo.Items.Add(new ComboBoxItem { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDB8DB0530432BD15), Tag = EmergencyAccessType.View });
        typeCombo.Items.Add(new ComboBoxItem { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEA878D5D090510DA), Tag = EmergencyAccessType.Takeover });
        typeCombo.SelectedIndex = item.Type == EmergencyAccessType.Takeover ? 1 : 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(typeCombo, "UpdateTypeCombo");

        var waitDaysBox = new NumberBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC745136D694620DE),
            Minimum = 1,
            Maximum = 90,
            Value = item.WaitTimeDays,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(waitDaysBox, "UpdateWaitDaysBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(typeCombo);
        var accessHelp = new TextBlock { Text = AccessConsequences(item.Type), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(accessHelp);
        panel.Children.Add(waitDaysBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextF49284F5654F4E9C,item.Email),
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA3030BF8F16DC63C),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        waitDaysBox.ValueChanged += (_, _) => dialog.IsPrimaryButtonEnabled = IsValidWaitTime(waitDaysBox.Value);
        typeCombo.SelectionChanged += (_, _) => accessHelp.Text = AccessConsequences(
            typeCombo.SelectedItem is ComboBoxItem { Tag: EmergencyAccessType type } ? type : item.Type);

        while (true)
        {
            var dialogResult = await DialogHelper.ShowTrackedAsync(dialog);
            if (dialogResult != ContentDialogResult.Primary)
                return;

            var type = typeCombo.SelectedItem is ComboBoxItem selectedType && selectedType.Tag is EmergencyAccessType t
                ? t
                : item.Type;
            var waitDays = double.IsNaN(waitDaysBox.Value) ? item.WaitTimeDays : (int)waitDaysBox.Value;

            var result = await ViewModel.UpdateAsync(item, type, waitDays);
            if (result.IsSuccess)
                return;

            errorText.Text = result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text11252DA5CAA95E13);
            errorText.Visibility = Visibility.Visible;
        }
    }


    private async void OnInitiateClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GrantedAccess item)
            return;
        if (!await DialogHelper.ConfirmAsync(XamlRoot, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text86243361EC69F55D),
            global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text830FDE0A49FE8357,item.Email,item.WaitTimeDays,AccessConsequences(item.Type)), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBDE8FB856741242C)))
            return;
        await ViewModel.InitiateAsync(item);
    }

    private async void OnViewClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GrantedAccess item)
            return;
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        VaultSessionLease lease;
        try { lease = session.CaptureLease(); }
        catch (InvalidOperationException) { return; }
        if (!session.IsUnlocked(lease))
            return;
        var result = await ViewModel.ViewAsync(item, lease.CancellationToken);
        if (result.IsSuccess && result.Value is { } recovered)
        {
            try
            {
                if (session.IsUnlocked(lease))
                    await ShowViewResultDialogAsync(recovered, lease);
            }
            finally
            {
                ViewModel.RecoveredItems.Remove(recovered);
            }
        }
    }

    private async Task ShowViewResultDialogAsync(RecoveredVault recovered, VaultSessionLease lease)
    {
        var services = global::App.App.Services;
        using var recovery = new RecoveredVaultViewSession(
            services.GetRequiredService<Vault.VaultSession>(),
            lease,
            recovered,
            services.GetRequiredService<IClipboardService>());
        var recoveredItems = recovered.Ciphers
            .Select(RecoveredCipherListItem.FromCipher)
            .ToArray();
        var list = new ListView
        {
            ItemsSource = recoveredItems,
            ItemTemplate = (DataTemplate)Resources["RecoveredCipherItemTemplate"],
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 180,
            MinHeight = recoveredItems.Length == 0 ? 0 : 80,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text36FCAE4C5D7B3270));

        var panel = new StackPanel { Spacing = 12, MinWidth = 240 };
        var summary = new TextBlock
        {
            Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text9EEE96D332F064D3,recovered.GrantorEmail,recovered.Ciphers.Count),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(summary);
        panel.Children.Add(list);
        var selectionTitle = new TextBlock
        {
            Text = recoveredItems.Length == 0 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text091EACCEA1621CD4) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0C3038CC76AFB221),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(selectionTitle);
        var password = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text56D4808B1D65DDFE), PasswordRevealMode = PasswordRevealMode.Hidden };
        var verify = new Button { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C34FC0126F7E437) };
        var verification = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        verification.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text45412CD695D29BEE),
            TextWrapping = TextWrapping.Wrap,
        });
        verification.Children.Add(password);
        verification.Children.Add(verify);
        panel.Children.Add(verification);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(
            feedback, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        panel.Children.Add(feedback);
        var fields = new StackPanel { Spacing = 12 };
        panel.Children.Add(new ScrollViewer
        {
            Content = fields,
            MaxHeight = 300,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
        });
        var displayedValues = new List<TextBlock>();

        void ClearDisplayedFields()
        {
            foreach (var value in displayedValues)
                value.Text = string.Empty;
            displayedValues.Clear();
            fields.Children.Clear();
        }

        void RenderFields()
        {
            ClearDisplayedFields();
            password.Password = string.Empty;
            verification.Visibility = recovery.RequiresVerification ? Visibility.Visible : Visibility.Collapsed;
            foreach (var field in recovery.Fields)
            {
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = field.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var value = new TextBlock { Text = field.DisplayValue, TextWrapping = TextWrapping.Wrap };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value, field.Label);
                displayedValues.Add(value);
                row.Children.Add(value);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (field.IsSecret)
                {
                    var reveal = new Button { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text36B830BDB447F8C7) };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(reveal, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text3B7C25E0D86BFBD8,field.Label));
                    reveal.Click += (_, _) =>
                    {
                        if (!recovery.ToggleReveal(field))
                            return;
                        value.Text = field.DisplayValue;
                        reveal.Content = field.IsRevealed ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAC20A57BFDE0BBC4) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text36B830BDB447F8C7);
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                            reveal, $"{(field.IsRevealed ? "Hide" : "Reveal")} {field.Label}");
                    };
                    actions.Children.Add(reveal);
                }
                var copy = new Button { Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE21F935F11D7E966) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextD4C4DA1DA997415B,field.Label));
                copy.Click += (_, _) =>
                {
                    try
                    {
                        if (recovery.Copy(field))
                            feedback.Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text4EA2BF6F51B950E9,field.Label);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        feedback.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6392C2FBA78CE1C2);
                    }
                };
                actions.Children.Add(copy);
                row.Children.Add(actions);
                fields.Children.Add(row);
            }
        }

        list.SelectionChanged += (_, _) =>
        {
            feedback.Text = string.Empty;
            if (list.SelectedIndex >= 0 && recovery.Select(list.SelectedIndex))
            {
                selectionTitle.Text = recoveredItems[list.SelectedIndex].Name;
                RenderFields();
            }
        };
        verify.Click += async (_, _) =>
        {
            var enteredPassword = password.Password;
            password.Password = string.Empty;
            verify.IsEnabled = false;
            try
            {
                var authorized = await recovery.VerifySelectionAsync(
                    token => services.GetRequiredService<IAccountService>().VerifyMasterPasswordAsync(enteredPassword, token),
                    lease.CancellationToken);
                if (authorized)
                    RenderFields();
                else if (!lease.CancellationToken.IsCancellationRequested)
                    feedback.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1BF2970F07726585);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (!lease.CancellationToken.IsCancellationRequested)
                    feedback.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text288413EFBDF841B8);
            }
            finally
            {
                enteredPassword = string.Empty;
                password.Password = string.Empty;
                verify.IsEnabled = true;
            }
        };

        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC70CD4C774FC23F9),
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 620,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7D9EB7ACB13E2462),
            XamlRoot = XamlRoot,
        };
        void ClearDialog()
        {
            recovery.ClearSensitive();
            void ClearControls()
            {
                password.Password = string.Empty;
                ClearDisplayedFields();
                list.ItemsSource = null;
                Array.Clear(recoveredItems);
                summary.Text = string.Empty;
                selectionTitle.Text = string.Empty;
                feedback.Text = string.Empty;
                verification.Visibility = Visibility.Collapsed;
            }
            if (dialog.DispatcherQueue.HasThreadAccess)
                ClearControls();
            else
                dialog.DispatcherQueue.TryEnqueue(ClearControls);
        }
        recovery.Invalidated += ClearDialog;
        try
        {
            await DialogHelper.ShowTrackedAsync(dialog, lease, clearSensitive: ClearDialog, ct: lease.CancellationToken);
        }
        finally
        {
            recovery.Invalidated -= ClearDialog;
            ClearDialog();
            dialog.Content = null;
        }
    }


    private async void OnTakeoverClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GrantedAccess item)
            return;
        await ShowTakeoverDialogAsync(item);
    }

    private async Task ShowTakeoverDialogAsync(GrantedAccess item)
    {
        var pwBox = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1F0AEF61509CC6C8) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(pwBox, "TakeoverNewPasswordBox");

        var confirmBox = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3024A2967DBA66BC) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(confirmBox, "TakeoverConfirmPasswordBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        errorText.SetValue(ForegroundProperty, Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextA8C5D4804287C425,item.Email,AccessConsequences(EmergencyAccessType.Takeover)),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(pwBox);
        panel.Children.Add(confirmBox);
        var acknowledgedBox = new CheckBox
        {
            Content = new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1D61395635E90EB1), TextWrapping = TextWrapping.Wrap },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(acknowledgedBox, "TakeoverConsequencesCheckBox");
        panel.Children.Add(acknowledgedBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8E4865FD24809D4C),
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8DF258D65B9A1593),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            IsPrimaryButtonEnabled = false,
            PrimaryButtonStyle = (Style)Resources["EmergencyDangerButtonStyle"],
        };
        void UpdateTakeoverState() => dialog.IsPrimaryButtonEnabled = acknowledgedBox.IsChecked == true
            && !string.IsNullOrWhiteSpace(pwBox.Password) && pwBox.Password == confirmBox.Password;
        acknowledgedBox.Checked += (_, _) => UpdateTakeoverState();
        acknowledgedBox.Unchecked += (_, _) => UpdateTakeoverState();
        pwBox.PasswordChanged += (_, _) => UpdateTakeoverState();
        confirmBox.PasswordChanged += (_, _) => UpdateTakeoverState();

        try
        {
            while (true)
            {
                var dialogResult = await DialogHelper.ShowTrackedAsync(
                    dialog,
                    () =>
                    {
                        pwBox.Password = string.Empty;
                        confirmBox.Password = string.Empty;
                    });
                if (dialogResult != ContentDialogResult.Primary)
                    return;

                if (acknowledgedBox.IsChecked != true || string.IsNullOrWhiteSpace(pwBox.Password)
                    || pwBox.Password != confirmBox.Password)
                {
                    errorText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text90C5893C9913EE6E);
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }

                var result = await ViewModel.TakeoverAsync(item, pwBox.Password);
                if (result.IsSuccess)
                    return;

                errorText.Text = result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF64EBBFC3588F284);
                errorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            pwBox.Password = string.Empty;
            confirmBox.Password = string.Empty;
        }
    }
}
