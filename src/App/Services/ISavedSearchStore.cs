namespace App.Services;

public interface ISavedSearchStore
{
    IReadOnlyList<SavedSearchView> GetAll();
    void Save(SavedSearchView view);
    void Delete(string name);
}
