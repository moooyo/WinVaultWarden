namespace Core.Enums;

// Vaultwarden/Bitwarden 协议定义 (db/models/send.rs):
// Text = 0, File = 1。
public enum SendType
{
    Text = 0,
    File = 1,
}
