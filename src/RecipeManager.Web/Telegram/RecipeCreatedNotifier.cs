using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using RecipeManager.Web.Messaging;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Telegram;

public record RecipeCreated(int RecipeId, int TenantId);

// Sees every saved recipe, whether it came from an MVC form, the API, the MCP server or the
// AI agent, and queues a Telegram notification (in memory or in RabbitMQ, B8). Sending happens
// in the background, so a slow or failing Telegram never delays or breaks the save itself.
public class RecipeCreatedNotifier : SaveChangesInterceptor
{
    private readonly IRecipeCreatedPublisher _publisher;
    private readonly bool _enabled;
    private readonly List<Recipe> _added = [];

    public RecipeCreatedNotifier(IRecipeCreatedPublisher publisher, IOptions<TelegramOptions> options)
    {
        _publisher = publisher;
        _enabled = options.Value.Enabled;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_enabled && eventData.Context != null)
            _added.AddRange(eventData.Context.ChangeTracker.Entries<Recipe>()
                .Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity));
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        // Ids exist only after the insert.
        var added = _added.ToList();
        _added.Clear();
        foreach (var recipe in added) await _publisher.PublishAsync(new RecipeCreated(recipe.Id, recipe.TenantId));
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _added.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }
}
