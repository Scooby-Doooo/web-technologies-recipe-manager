namespace RecipeManager.Web.Models;

// The notes document of a recipe (C12), stored on the server since C13 so that every device
// edits the same document. Belongs to the recipe's kitchen through the recipe.
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
