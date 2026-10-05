using Microsoft.AspNetCore.OutputCaching;

namespace RecipeManager.Web.Data;

// The default output cache policy skips signed-in requests. API data is the same for every
// user, so this policy caches GET/HEAD responses regardless of the auth cookie or token,
// and keeps the other default rules: only 200 responses and never responses that set cookies.
public sealed class PublicApiCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var method = context.HttpContext.Request.Method;
        var cacheable = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        context.EnableOutputCaching = true;
        context.AllowCacheLookup = cacheable;
        context.AllowCacheStorage = cacheable;
        context.AllowLocking = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        if (response.StatusCode != StatusCodes.Status200OK || response.Headers.SetCookie.Count > 0)
            context.AllowCacheStorage = false;
        return ValueTask.CompletedTask;
    }
}
