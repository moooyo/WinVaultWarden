using Core.Models;

namespace Core.Abstractions;

public interface ITokenStore
{
    bool TryLoad(out PersistedSession session);
    void Save(PersistedSession session);
    void Clear();
}
