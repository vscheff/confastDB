using Confast.Web.Features.Navigation;

namespace Confast.Web.Components.Layout;

// The account store is authoritative. This coordinates the two editors within
// one circuit and keeps committed preferences in its render tree.
internal sealed class AppearanceSession(AppearancePreferenceService preferences)
{
    private readonly SemaphoreSlim operations = new(1);
    internal AppearancePreference? Current { get; private set; }
    internal event Action<AppearancePreference>? Changed;

    internal Task<AppearancePreference> LoadAsync() => PublishAsync(preferences.LoadAsync);

    internal Task<AppearancePreference> SaveAsync(AppearanceMode? mode, string? accent) =>
        PublishAsync(() => preferences.SaveAsync(mode, accent));

    private async Task<AppearancePreference> PublishAsync(Func<Task<AppearancePreference>> operation)
    {
        await operations.WaitAsync();
        try
        {
            var updated = await operation();
            if (updated != Current)
            {
                Current = updated;
                Changed?.Invoke(updated);
            }
            return updated;
        }
        finally { operations.Release(); }
    }
}
