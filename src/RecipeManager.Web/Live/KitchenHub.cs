using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Live;

// SignalR hub: each connection joins its kitchen group for the live feed. It also syncs recipe notes.
public class KitchenHub : Hub
{
    private readonly KitchenFeed _feed;
    private readonly ApplicationDbContext _context;
    private readonly TenantContext _tenant;

    public KitchenHub(KitchenFeed feed, ApplicationDbContext context, TenantContext tenant)
    {
        _feed = feed;
        _context = context;
        _tenant = tenant;
    }

    public static string Group(int tenantId) => "kitchen-" + tenantId;

    // Recipe ids are unique across kitchens; a connection joins only after its kitchen is checked.
    public static string NotesGroup(int recipeId) => "notes-" + recipeId;

    // The kitchen comes from the connection's own user; hubs should not read IHttpContextAccessor.
    private int TenantId => TenantContext.FromUser(Context.User);

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(TenantId));
        await base.OnConnectedAsync();
    }

    // After (re)connecting the client asks for the events it missed.
    public object Since(long since)
    {
        var (last, events) = _feed.Since(TenantId, since);
        return new { last, events };
    }

    // Joins the notes group and returns the current document. Called again after a reconnect.
    [Authorize]
    public async Task<object> OpenNotes(int recipeId)
    {
        var (_, version, blocks) = await LoadNotes(recipeId);
        await Groups.AddToGroupAsync(Context.ConnectionId, NotesGroup(recipeId));
        OpenedNotes.Add(recipeId);
        // The copies already open answer with the block they are in.
        await Clients.OthersInGroup(NotesGroup(recipeId)).SendAsync("joined");
        return new { version, blocks };
    }

    // Tells the other open copies which block this one is in, or null when it left the document.
    // Only notes this connection opened (and so passed the kitchen check) are accepted.
    [Authorize]
    public Task FocusNotes(int recipeId, string? blockId)
    {
        if (!OpenedNotes.Contains(recipeId)) throw new HubException("Open the notes first.");
        if (blockId?.Length > 64) throw new HubException("The change has an invalid block id.");
        return Clients.OthersInGroup(NotesGroup(recipeId)).SendAsync("focus", new { connection = Context.ConnectionId, block = blockId });
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // A closed copy is no longer anywhere in the document.
        foreach (var recipeId in OpenedNotes)
            await Clients.OthersInGroup(NotesGroup(recipeId)).SendAsync("focus", new { connection = Context.ConnectionId, block = (string?)null });
        await base.OnDisconnectedAsync(exception);
    }

    // Context.Items lives as long as the connection, unlike the hub object, which is made per call.
    private HashSet<int> OpenedNotes => (HashSet<int>)(Context.Items.TryGetValue("notes", out var opened)
        ? opened!
        : Context.Items["notes"] = new HashSet<int>());

    // Applies a change and sends the new document to everyone, the sender included (that is its confirmation).
    [Authorize]
    public async Task SaveNotes(int recipeId, NotesChange change)
    {
        if (change is null) throw new HubException("The change is empty.");
        change.Validate();
        for (var attempt = 1; ; attempt++)
        {
            var (notes, version, blocks) = await LoadNotes(recipeId);
            var result = change.ApplyTo(blocks);
            if (result.Count > NotesChange.MaxBlocks) throw new HubException($"Notes can have up to {NotesChange.MaxBlocks} blocks.");
            if (notes is null) _context.RecipeNotes.Add(notes = new RecipeNotes { RecipeId = recipeId });
            notes.Version = version + 1;
            notes.Blocks = JsonSerializer.Serialize(result, JsonSerializerOptions.Web);
            notes.UpdatedAt = DateTime.UtcNow;
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Another device saved first (Version is a concurrency token): apply the change to its result.
                _context.ChangeTracker.Clear();
                continue;
            }
            await Clients.Group(NotesGroup(recipeId)).SendAsync("notes", new { version = notes.Version, blocks = result });
            return;
        }
    }

    private async Task<(RecipeNotes? Notes, int Version, List<NoteBlock> Blocks)> LoadNotes(int recipeId)
    {
        // Hub methods have no request of their own, so the kitchen for the query filters is set here.
        _tenant.UseTenant(TenantId);
        var title = await _context.Recipes.Where(recipe => recipe.Id == recipeId).Select(recipe => recipe.Title).FirstOrDefaultAsync()
                    ?? throw new HubException("The recipe was not found.");
        var notes = await _context.RecipeNotes.FirstOrDefaultAsync(item => item.RecipeId == recipeId);
        return notes is null
            ? (null, 0, NotesChange.Initial(title))
            : (notes, notes.Version, JsonSerializer.Deserialize<List<NoteBlock>>(notes.Blocks, JsonSerializerOptions.Web) ?? []);
    }
}
