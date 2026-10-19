using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Live;

// Publishes every saved recipe change to the kitchen feed, whichever page, API or agent made it,
// and pushes it to the kitchen's SignalR group (C11).
public class KitchenFeedInterceptor : SaveChangesInterceptor
{
    private readonly KitchenFeed _feed;
    private readonly IHubContext<KitchenHub> _hub;
    private readonly ILogger<KitchenFeedInterceptor> _logger;
    private readonly List<(Recipe Recipe, string Type)> _changed = [];

    public KitchenFeedInterceptor(KitchenFeed feed, IHubContext<KitchenHub> hub, ILogger<KitchenFeedInterceptor> logger)
    {
        _feed = feed;
        _hub = hub;
        _logger = logger;
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

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        // Ids of new recipes exist only after the insert.
        var changed = _changed.ToList();
        _changed.Clear();
        foreach (var (recipe, type) in changed)
        {
            var added = _feed.Publish(recipe.TenantId, type, recipe.Id, recipe.Title);
            try
            {
                // Sending only queues the message for each connection, so the save does not wait for clients.
                await _hub.Clients.Group(KitchenHub.Group(recipe.TenantId)).SendAsync("changes",
                    new { last = added.Id, events = new[] { added } }, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // The change is already saved; a failed push must not turn it into an error page.
                _logger.LogWarning(exception, "Unable to push a kitchen change to SignalR clients");
            }
        }
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _changed.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}
