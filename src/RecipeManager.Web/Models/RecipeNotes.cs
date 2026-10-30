namespace RecipeManager.Web.Models;

// The notes document of a recipe, shared by all devices.
public class RecipeNotes
{
    public int RecipeId { get; set; }

    public Recipe Recipe { get; set; } = null!;

    // Grows by one with every saved change; clients use it to skip messages older than what they have.
    public int Version { get; set; }

    // The blocks as JSON: [{ "id", "type", "text", "checked" }].
    public string Blocks { get; set; } = "[]";

    public DateTime UpdatedAt { get; set; }
}
