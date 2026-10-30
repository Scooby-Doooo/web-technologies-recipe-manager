using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Data;

// Evicts cached API responses after every save, from the forms or the API. Recipe responses
// include category and ingredient names, so those changes evict recipes too.
public class OutputCacheInvalidationInterceptor : SaveChangesInterceptor
{
    public const string RecipesTag = "api-recipes";
    public const string CategoriesTag = "api-categories";
    public const string IngredientsTag = "api-ingredients";

    private readonly IOutputCacheStore _cache;
    private readonly ILogger<OutputCacheInvalidationInterceptor> _logger;
    private readonly HashSet<string> _pendingTags = [];

    public OutputCacheInvalidationInterceptor(IOutputCacheStore cache, ILogger<OutputCacheInvalidationInterceptor> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        CollectTags(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        foreach (var tag in _pendingTags)
        {
            await _cache.EvictByTagAsync(tag, CancellationToken.None);
            _logger.LogInformation("Evicted cached API responses tagged {Tag}", tag);
        }
        _pendingTags.Clear();
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _pendingTags.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void CollectTags(DbContext? context)
    {
        if (context == null) return;
        var changed = context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity);
        foreach (var entity in changed)
        {
            switch (entity)
            {
                case Category:
                    _pendingTags.Add(CategoriesTag);
                    _pendingTags.Add(RecipesTag);
                    break;
                case Ingredient:
                    _pendingTags.Add(IngredientsTag);
                    _pendingTags.Add(RecipesTag);
                    break;
                case Recipe or RecipeIngredient:
                    _pendingTags.Add(RecipesTag);
                    break;
            }
        }
    }
}
