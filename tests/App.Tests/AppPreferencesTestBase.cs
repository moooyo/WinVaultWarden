using App.Services;
using Xunit;

namespace App.Tests;

public static class TestCollectionNames
{
    public const string AppPreferences = "App preferences";
}

[CollectionDefinition(TestCollectionNames.AppPreferences, DisableParallelization = true)]
public sealed class AppPreferencesTestCollection;

public abstract class AppPreferencesTestBase : IDisposable
{
    private readonly IDisposable _scope = AppPreferences.UseInMemoryForTesting();

    public void Dispose() => _scope.Dispose();
}
