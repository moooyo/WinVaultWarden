namespace Core.Enums;

// Vaultwarden/Bitwarden 协议定义 (db/models/send.rs):
// Text = 0, File = 1。
public enum SendType
{
    Text = 0,
    File = 1,
}

public static class SendTypePolicy
{
    public static SendType RequireSupported(int rawType) => rawType switch
    {
        (int)SendType.Text => SendType.Text,
        (int)SendType.File => SendType.File,
        _ => throw new NotSupportedException(
            $"Send type {rawType} is not supported by this client."),
    };
}
