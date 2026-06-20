using Core.Models;

namespace Core.Services;

public interface IVaultService
{
    IReadOnlyList<Cipher> GetCiphers();
}
