using Microsoft.AspNetCore.OutputCaching;

namespace RecipeManager.Web.Data;

// The default output cache policy skips signed-in requests. API data depends only on the
// kitchen, so this policy caches GET/HEAD responses per kitchen regardless of the auth cookie
// or token, and keeps the other default rules: only 200 responses and never responses that set cookies.
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
        context.CacheVaryByRules.VaryByValues["tenant"] =
            context.HttpContext.RequestServices.GetRequiredService<TenantContext>().TenantId.ToString();
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
