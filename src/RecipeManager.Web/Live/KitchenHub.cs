using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Live;

// SignalR version of the live feed (C11). Each connection joins the group of its kitchen, and
// KitchenFeedInterceptor sends every change to that group as a "changes" message.
// Since C13 the same hub keeps recipe notes in sync between devices.
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

    // After connecting or reconnecting the client asks for what it missed, in the same shape as the C10 answers.
    public object Since(long since)
    {
        var (last, events) = _feed.Since(TenantId, since);
        return new { last, events };
    }

    // Opens the notes of a recipe: the connection gets every later change and the current document.
    // A reconnect is a new connection, so the client opens the notes again after it.
    [Authorize]
    public async Task<object> OpenNotes(int recipeId)
    {
        var (_, version, blocks) = await LoadNotes(recipeId);
        await Groups.AddToGroupAsync(Context.ConnectionId, NotesGroup(recipeId));
        return new { version, blocks };
    }

    // Applies one editor's change and sends the new document to everyone who has the notes open,
    // the sender included: that answer is how the sender learns its change is saved.
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
