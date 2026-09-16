namespace App.ViewModels.Models;

// Runtime Binding in reusable DataTemplates needs generated property accessors
// under Native AOT. Keep WinUI metadata separate from the models linked by App.Tests.
// CsWinRT includes inherited public properties and exposes init-only values as read-only.
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class LoginDetail { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CardDetail { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class IdentityDetail { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class NoteDetail { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class SshDetail { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class BankAccountDetail { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DriversLicenseDetail { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PassportDetail { }

[WinRT.GeneratedBindableCustomProperty]
public partial record CustomField { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record PasswordHistoryItem { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record PasskeyDetail { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CipherListItem { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record AttachmentItem { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record CipherDestinationOption { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CipherCollectionOption { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class FilterNode { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class LoginUriEditorDraft { }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CustomFieldEditorDraft { }
