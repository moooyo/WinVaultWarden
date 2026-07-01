namespace App.Services;

public sealed class AppPreferencesSavedSearchStore : ISavedSearchStore
{
    public IReadOnlyList<SavedSearchView> GetAll() =>
        AppPreferences.Current.SavedSearchViews.Select(d => d.ToView()).ToList();

    public void Save(SavedSearchView view)
    {
        var list = AppPreferences.Current.SavedSearchViews;
        SavedSearchOps.Upsert(list, SavedSearchViewData.FromView(view));
        AppPreferences.Save();
    }

    public void Delete(string name)
    {
        var list = AppPreferences.Current.SavedSearchViews;
        SavedSearchOps.Remove(list, name);
        AppPreferences.Save();
    }
}
