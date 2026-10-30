using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Search;

// Marks a kitchen's search index stale after saving recipes, categories, ingredients or their links.
public class RecipeSearchInterceptor : SaveChangesInterceptor
{
    private readonly RecipeSearchIndex _index;
    private readonly TenantContext _tenant;
    private readonly HashSet<int> _pending = [];

    public RecipeSearchInterceptor(RecipeSearchIndex index, TenantContext tenant)
    {
        _index = index;
        _tenant = tenant;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var changed = eventData.Context?.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity) ?? [];
        foreach (var entity in changed)
        {
            switch (entity)
            {
                case Recipe or Category or Ingredient:
                    _pending.Add(((ITenantOwned)entity).TenantId);
                    break;
                // A link has no kitchen of its own; it is saved in the kitchen of the request.
                case RecipeIngredient:
                    _pending.Add(_tenant.TenantId);
                    break;
            }
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        foreach (var tenantId in _pending) _index.MarkChanged(tenantId);
        _pending.Clear();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}
