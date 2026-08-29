namespace Core.Enums;

/// <summary>
/// Selects the transport used for an encrypted file body after its metadata is created.
/// </summary>
public enum FileUploadType
{
    Direct = 0,
    Azure = 1,
}
