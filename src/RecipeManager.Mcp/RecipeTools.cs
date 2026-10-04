using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RecipeManager.Mcp;

public record RecipeSummary(int Id, string Title, string CategoryName, int CookingTimeMinutes, int Servings);

[McpServerToolType]
public class RecipeTools
{
    private const int PageSize = 100;
    private readonly RecipeApiClient _api;

    public RecipeTools(RecipeApiClient api)
    {
        _api = api;
    }

    [McpServerTool(Name = "list_categories", Title = "List categories", ReadOnly = true)]
    [Description("Lists all recipe categories with their IDs. Use an ID from here when creating a recipe.")]
    public async Task<IReadOnlyList<Category>> ListCategories(CancellationToken cancellationToken)
    {
        var categories = new List<Category>();
        await foreach (var category in ReadAll<Category>("api/categories", cancellationToken))
            categories.Add(category);
        return categories;
    }

    [McpServerTool(Name = "search_recipes", Title = "Search recipes", ReadOnly = true)]
    [Description("Finds recipes by text in the title or description, category and maximum cooking time. " +
                 "All filters are optional; without filters the first recipes in alphabetical order are returned.")]
    public async Task<IReadOnlyList<RecipeSummary>> SearchRecipes(
        [Description("Text to look for in the title or description, case-insensitive.")] string? text = null,
        [Description("Only recipes from this category ID.")] int? categoryId = null,
        [Description("Only recipes that take at most this many minutes.")] int? maxCookingTimeMinutes = null,
        [Description("Maximum number of results, 1-50.")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var results = new List<RecipeSummary>();
        // The API has no search endpoint, so the tool walks the paginated list (B1) and filters it.
        await foreach (var recipe in ReadAll<Recipe>("api/recipes", cancellationToken))
        {
            if (text != null && !recipe.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                && recipe.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) != true) continue;
            if (categoryId != null && recipe.CategoryId != categoryId) continue;
            if (maxCookingTimeMinutes != null && recipe.CookingTimeMinutes > maxCookingTimeMinutes) continue;
            results.Add(new RecipeSummary(recipe.Id, recipe.Title, recipe.CategoryName, recipe.CookingTimeMinutes, recipe.Servings));
            if (results.Count == limit) break;
        }
        return results;
    }

    [McpServerTool(Name = "get_recipe", Title = "Get recipe", ReadOnly = true)]
    [Description("Returns one recipe with its description, step-by-step instructions, ingredients and photo path.")]
    public Task<Recipe> GetRecipe(
        [Description("Recipe ID, for example from search_recipes.")] int id,
        CancellationToken cancellationToken) => _api.GetRecipe(id, cancellationToken);

    [McpServerTool(Name = "create_category", Title = "Create category", Destructive = false, Idempotent = false)]
    [Description("Creates a new recipe category and returns it with its ID.")]
    public Task<Category> CreateCategory(
        [Description("Category name, up to 100 characters.")] string name,
        CancellationToken cancellationToken) =>
        _api.Post<Category>("api/categories", new { name }, cancellationToken);

    [McpServerTool(Name = "create_recipe", Title = "Create recipe", Destructive = false, Idempotent = false)]
    [Description("Saves a new recipe in Recipe Manager and returns it with its ID. " +
                 "Put each instruction step on its own line.")]
    public Task<Recipe> CreateRecipe(
        [Description("Recipe title, up to 200 characters.")] string title,
        [Description("Step-by-step instructions, one step per line.")] string instructions,
        [Description("Cooking time in minutes, at least 1.")] int cookingTimeMinutes,
        [Description("Number of servings, at least 1.")] int servings,
        [Description("Existing category ID from list_categories.")] int categoryId,
        [Description("Optional short description, up to 2000 characters.")] string? description = null,
        CancellationToken cancellationToken = default) =>
        _api.Post<Recipe>("api/recipes",
            new { title, description, instructions, cookingTimeMinutes, servings, categoryId }, cancellationToken);

    private async IAsyncEnumerable<T> ReadAll<T>(string resource,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? next = $"{resource}?skip=0&limit={PageSize}";
        while (next != null)
        {
            var page = await _api.GetPage<T>(next, cancellationToken);
            foreach (var item in page.Items) yield return item;
            next = page.NextLink;
        }
    }
}
