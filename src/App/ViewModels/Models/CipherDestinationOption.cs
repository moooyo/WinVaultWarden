using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels.Models;

public sealed record CipherDestinationOption(
    string DisplayName,
    string? OrganizationId,
    string? CollectionId)
{
    public static CipherDestinationOption Personal { get; } =
        new("个人密码库", null, null);
}

public sealed partial class CipherCollectionOption : ObservableObject
{
    public required string Id { get; init; }
    public required string OrganizationId { get; init; }
    public required string Name { get; init; }
    public bool IsLocked { get; init; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool CanSelect => !IsLocked;
}
