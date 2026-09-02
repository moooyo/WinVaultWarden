namespace Core.Enums;

public enum OrganizationPolicyType
{
    TwoFactorAuthentication = 0,
    MasterPassword = 1,
    PasswordGenerator = 2,
    SingleOrganization = 3,
    RequireSso = 4,
    PersonalOwnership = 5,
    DisableSend = 6,
    SendOptions = 7,
    ResetPassword = 8,
    MaximumVaultTimeout = 9,
    DisablePersonalVaultExport = 10,
    ActivateAutofill = 11,
    AutomaticAppLogIn = 12,
    FreeFamiliesSponsorship = 13,
    RemoveUnlockWithPin = 14,
    RestrictedItemTypes = 15,
    UriMatchDefaults = 16,
    AutotypeDefaultSetting = 17,
    AutomaticUserConfirmation = 18,
    BlockClaimedDomainAccountCreation = 19,
    OrganizationUserNotification = 20,
    SendControls = 21,
    FillAssist = 22,
}
