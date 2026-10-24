using Microsoft.AspNetCore.SignalR;

namespace RecipeManager.Web.Live;

public record NoteBlock(string Id, string Type, string Text, bool Checked);

// What one editor changed since the version it last received: blocks that are new or edited,
// blocks that were removed and, when blocks moved or were added, the new order of ids.
// Applying the same change twice gives the same document, so a client can resend it safely.
public record NotesChange(NoteBlock[]? Upserts, string[]? Deletes, string[]? Order)
{
    public const int MaxBlocks = 300;
    public const int MaxText = 5000;
    private static readonly HashSet<string> Types =
        ["paragraph", "heading1", "heading2", "heading3", "bullet", "numbered", "todo", "quote"];

    // The document a recipe has before anyone saves it. The ids are fixed, so two devices that
    // open a new document at once edit the same two blocks instead of adding a copy each.
    public static List<NoteBlock> Initial(string title) =>
        [new("title", "heading1", "Notes for " + title, false), new("start", "paragraph", "", false)];

    // The change comes from a browser, so it is checked like any other input.
    public void Validate()
    {
        if ((Upserts?.Length ?? 0) > MaxBlocks || (Deletes?.Length ?? 0) > MaxBlocks * 2 || (Order?.Length ?? 0) > MaxBlocks * 2)
            throw new HubException("The change is too large.");
        foreach (var block in Upserts ?? [])
        {
            if (block is null || string.IsNullOrEmpty(block.Id) || block.Id.Length > 64 || !Types.Contains(block.Type) || block.Text is null)
                throw new HubException("The change has an invalid block.");
            if (block.Text.Length > MaxText) throw new HubException($"A block can hold up to {MaxText} characters.");
        }
        if (Upserts is not null && Upserts.DistinctBy(block => block.Id).Count() != Upserts.Length)
            throw new HubException("The change has the same block twice.");
        if (Deletes?.Any(id => id is null) == true || Order?.Any(id => id is null) == true)
            throw new HubException("The change has an invalid block id.");
    }

    // The same steps run in notes-editor.js when a client puts its unsent edits on top of a newer document.
    public List<NoteBlock> ApplyTo(IEnumerable<NoteBlock> blocks)
    {
        var deleted = new HashSet<string>(Deletes ?? []);
        var result = blocks.Where(block => !deleted.Contains(block.Id)).ToList();
        foreach (var block in Upserts ?? [])
        {
            var index = result.FindIndex(item => item.Id == block.Id);
            if (index >= 0) result[index] = block;
            else result.Add(block);
        }
        if (Order is null) return result;

        // Blocks this editor did not know about (added on another device meanwhile) keep their
        // place: each goes right after the block that was before it.
        var byId = result.ToDictionary(block => block.Id);
        var placed = Order.Distinct().Where(byId.ContainsKey).ToList();
        string? previous = null;
        foreach (var block in result)
        {
            if (!placed.Contains(block.Id)) placed.Insert(previous is null ? 0 : placed.IndexOf(previous) + 1, block.Id);
            previous = block.Id;
        }
        return placed.Select(id => byId[id]).ToList();
    }
}
