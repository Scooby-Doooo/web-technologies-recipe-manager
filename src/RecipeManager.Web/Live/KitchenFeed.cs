using System.Collections.Concurrent;

namespace RecipeManager.Web.Live;

// At is the server time in Unix milliseconds, so a client can tell how late an event arrived.
public record KitchenEvent(long Id, string Type, int RecipeId, string Title, long At);

// The latest recipe changes of every kitchen for the live page (C10). Each kitchen numbers its
// own events, so a client asks for "everything after N" and the numbers reveal nothing about
// other kitchens.
// shortcut: kept in the memory of one process, so with several app instances each one sees only
// its own saves; a shared message bus (Redis, a SignalR backplane) is the upgrade for that.
public class KitchenFeed
{
    private const int Capacity = 50;
    private readonly ConcurrentDictionary<int, Kitchen> _kitchens = new();

    private sealed class Kitchen
    {
        public readonly List<KitchenEvent> Events = [];
        public long LastId;
        public TaskCompletionSource Changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public KitchenEvent Publish(int tenantId, string type, int recipeId, string title)
    {
        var kitchen = _kitchens.GetOrAdd(tenantId, _ => new Kitchen());
        TaskCompletionSource changed;
        KitchenEvent added;
        lock (kitchen)
        {
            added = new KitchenEvent(++kitchen.LastId, type, recipeId, title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            kitchen.Events.Add(added);
            if (kitchen.Events.Count > Capacity) kitchen.Events.RemoveAt(0);
            changed = kitchen.Changed;
            kitchen.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        // Wakes every long poll and WebSocket waiting for this kitchen.
        changed.SetResult();
        return added;
    }

    // Events after `since`. A `since` ahead of the feed means the server restarted, so the client starts over.
    public (long Last, IReadOnlyList<KitchenEvent> Events) Since(int tenantId, long since)
    {
        if (!_kitchens.TryGetValue(tenantId, out var kitchen)) return (0, []);
        lock (kitchen)
        {
            if (since > kitchen.LastId) since = 0;
            return (kitchen.LastId, kitchen.Events.Where(item => item.Id > since).ToList());
        }
    }

    // Returns when the kitchen has something other than `since`, when `timeout` passes or when cancelled.
    public async Task WaitAsync(int tenantId, long since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var kitchen = _kitchens.GetOrAdd(tenantId, _ => new Kitchen());
        Task changed;
        lock (kitchen)
        {
            if (kitchen.LastId != since) return;
            changed = kitchen.Changed.Task;
        }
        // Task.Delay ends as cancelled when the client leaves; WhenAny does not throw for that.
        await Task.WhenAny(changed, Task.Delay(timeout, cancellationToken));
    }
}
