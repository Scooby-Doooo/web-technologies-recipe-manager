using System.Globalization;

namespace RecipeManager.Web.Models;

// Builds schema.org/Recipe data that search engines read from JSON-LD.
public static class RecipeStructuredData
{
    public static Dictionary<string, object> Create(Recipe recipe, string pageUrl, string? imageUrl)
    {
        var data = new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Recipe",
            ["name"] = recipe.Title,
            ["url"] = pageUrl,
            ["description"] = Summary(recipe),
            ["recipeCategory"] = recipe.Category.Name,
            ["cookTime"] = "PT" + recipe.CookingTimeMinutes + "M",
            ["totalTime"] = "PT" + recipe.CookingTimeMinutes + "M",
            ["recipeYield"] = recipe.Servings + (recipe.Servings == 1 ? " serving" : " servings"),
            ["recipeIngredient"] = recipe.RecipeIngredients
                .Select(item => $"{item.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} {item.Unit} {item.Ingredient.Name}")
                .ToArray(),
            ["recipeInstructions"] = recipe.Instructions
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(step => new Dictionary<string, string> { ["@type"] = "HowToStep", ["text"] = step })
                .ToArray()
        };
        if (imageUrl != null) data["image"] = new[] { imageUrl };
        return data;
    }

    // Meta descriptions are usually cut at about 160 characters in search results.
    public static string Summary(Recipe recipe)
    {
        var text = string.IsNullOrWhiteSpace(recipe.Description)
            ? $"{recipe.Title}: {recipe.Category.Name.ToLowerInvariant()} recipe for {recipe.Servings} servings, ready in {recipe.CookingTimeMinutes} minutes."
            : recipe.Description.Trim();
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 160 ? text : text[..157].TrimEnd() + "...";
    }
}
