namespace Confast.Web.Features.Authorization;

// Process-local invalidation hints, with no actor/account/content payload. Database epochs remain authoritative.
public sealed class AuthorizationChangeNotifications(ILogger<AuthorizationChangeNotifications> logger)
{
    public event Action? Changed;
    public void Publish()
    {
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)subscriber)(); }
            // A failed advisory subscriber must not turn a committed security edit into an apparent failed save.
            catch (Exception exception) { logger.LogWarning(exception, "Authorization advisory invalidation subscriber failed"); }
        }
    }
}
