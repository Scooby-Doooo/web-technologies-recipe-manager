using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Live;

// Publishes every saved recipe change to the kitchen feed, whichever page, API or agent made it.
public class KitchenFeedInterceptor : SaveChangesInterceptor
{
    private readonly KitchenFeed _feed;
    private readonly List<(Recipe Recipe, string Type)> _changed = [];

    public KitchenFeedInterceptor(KitchenFeed feed)
    {
        _feed = feed;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context != null)
        {
            foreach (var entry in eventData.Context.ChangeTracker.Entries<Recipe>())
            {
                var type = entry.State switch
                {
                    EntityState.Added => "created",
                    EntityState.Modified => "updated",
                    EntityState.Deleted => "deleted",
                    _ => null
                };
                if (type != null) _changed.Add((entry.Entity, type));
            }
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        // Ids of new recipes exist only after the insert.
        foreach (var (recipe, type) in _changed) _feed.Publish(recipe.TenantId, type, recipe.Id, recipe.Title);
        _changed.Clear();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _changed.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}
