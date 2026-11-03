using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Features;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Assistant;

// Tools the model can call. They run in the user's request, so the kitchen filters apply.
public class AssistantTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApplicationDbContext _context;
    private readonly RecipeLimit _limit;

    public AssistantTools(ApplicationDbContext context, RecipeLimit limit)
    {
        _context = context;
        _limit = limit;
    }

    // OpenAI-style tool definitions; the descriptions tell the model when to use each one.
    public static readonly JsonArray Definitions = (JsonArray)JsonNode.Parse("""
    [
      { "type": "function", "function": {
        "name": "search_recipes",
        "description": "Find recipes in the user's kitchen. All filters are optional; without filters it lists recipes.",
        "parameters": { "type": "object", "properties": {
          "query": { "type": "string", "description": "Part of the recipe title" },
          "category": { "type": "string", "description": "Category name, e.g. Dessert" },
          "max_cooking_minutes": { "type": "integer", "description": "Only recipes that take at most this many minutes" }
        } } } },
      { "type": "function", "function": {
        "name": "get_recipe",
        "description": "Read one recipe with its ingredients and instructions.",
        "parameters": { "type": "object", "properties": {
          "id": { "type": "integer", "description": "Recipe id from search_recipes" }
        }, "required": ["id"] } } },
      { "type": "function", "function": {
        "name": "list_categories",
        "description": "List the kitchen's categories with their ids and how many recipes each has.",
        "parameters": { "type": "object", "properties": {} } } },
      { "type": "function", "function": {
        "name": "kitchen_statistics",
        "description": "Totals for the kitchen: number of recipes, recipes per category, average and shortest cooking time.",
        "parameters": { "type": "object", "properties": {} } } },
      { "type": "function", "function": {
        "name": "create_recipe",
        "description": "Save a new recipe in the user's kitchen. Use it only when the user asks to add or save a recipe.",
        "parameters": { "type": "object", "properties": {
          "title": { "type": "string" },
          "description": { "type": "string", "description": "One or two sentences, optional" },
          "instructions": { "type": "string", "description": "Steps, one per line" },
          "cooking_time_minutes": { "type": "integer" },
          "servings": { "type": "integer" },
          "category_id": { "type": "integer", "description": "Id from list_categories" },
          "ingredients": { "type": "array", "description": "Each ingredient once; new names are added to the kitchen",
            "items": { "type": "object", "properties": {
              "name": { "type": "string" },
              "quantity": { "type": "number", "description": "Greater than 0" },
              "unit": { "type": "string", "description": "e.g. g, ml, pcs, tbsp" }
            }, "required": ["name", "quantity", "unit"] } }
        }, "required": ["title", "instructions", "cooking_time_minutes", "servings", "category_id"] } } }
    ]
    """)!;

    // Returns JSON for the model. Bad arguments become an error message the model can react to.
    public async Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var arguments = document.RootElement;
            object result = name switch
            {
                "search_recipes" => await SearchAsync(arguments),
                "get_recipe" => await GetRecipeAsync(Int(arguments, "id") ?? 0),
                "list_categories" => await ListCategoriesAsync(),
                "kitchen_statistics" => await StatisticsAsync(),
                "create_recipe" => await CreateAsync(arguments),
                _ => new { error = $"Unknown tool {name}." }
            };
            return JsonSerializer.Serialize(result, Json);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { error = "Arguments are not valid JSON." }, Json);
        }
    }

    private async Task<object> SearchAsync(JsonElement arguments)
    {
        var recipes = _context.Recipes.AsNoTracking().AsQueryable();
        if (Text(arguments, "query") is { Length: > 0 } query)
            recipes = recipes.Where(item => EF.Functions.ILike(item.Title, "%" + Escape(query) + "%", "\\"));
        if (Text(arguments, "category") is { Length: > 0 } category)
            recipes = recipes.Where(item => EF.Functions.ILike(item.Category.Name, Escape(category), "\\"));
        if (Int(arguments, "max_cooking_minutes") is { } minutes)
            recipes = recipes.Where(item => item.CookingTimeMinutes <= minutes);
        var found = await recipes.OrderBy(item => item.Title).Take(20)
            .Select(item => new { item.Id, item.Title, Category = item.Category.Name, item.CookingTimeMinutes, item.Servings })
            .ToListAsync();
        return new { count = found.Count, recipes = found };
    }

    private async Task<object> GetRecipeAsync(int id)
    {
        var recipe = await _context.Recipes.AsNoTracking()
            .Include(item => item.Category)
            .Include(item => item.RecipeIngredients).ThenInclude(item => item.Ingredient)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (recipe == null) return new { error = $"No recipe with id {id} in this kitchen." };
        return new
        {
            recipe.Id, recipe.Title, Category = recipe.Category.Name, recipe.Description, recipe.Instructions,
            recipe.CookingTimeMinutes, recipe.Servings,
            Ingredients = recipe.RecipeIngredients.Select(item => new { item.Ingredient.Name, item.Quantity, item.Unit }),
            Url = $"/Recipes/Details/{recipe.Id}"
        };
    }

    private async Task<object> ListCategoriesAsync() => await _context.Categories.AsNoTracking().OrderBy(item => item.Name)
        .Select(item => new { item.Id, item.Name, Recipes = item.Recipes.Count })
        .ToListAsync();

    private async Task<object> StatisticsAsync()
    {
        var total = await _context.Recipes.CountAsync();
        var perCategory = await _context.Recipes.GroupBy(item => item.Category.Name)
            .Select(group => new { Category = group.Key, Recipes = group.Count() }).ToListAsync();
        return new
        {
            total,
            perCategory,
            averageCookingMinutes = total == 0 ? 0 : Math.Round(await _context.Recipes.AverageAsync(item => item.CookingTimeMinutes), 1),
            shortestCookingMinutes = total == 0 ? 0 : await _context.Recipes.MinAsync(item => item.CookingTimeMinutes)
        };
    }

    // Same checks as the form and the API: validation, category of this kitchen, Free plan limit.
    private async Task<object> CreateAsync(JsonElement arguments)
    {
        if (!await _limit.CanAddRecipeAsync()) return new { error = _limit.Message };
        var recipe = new Recipe
        {
            Title = Text(arguments, "title")?.Trim() ?? string.Empty,
            Description = Text(arguments, "description")?.Trim() is { Length: > 0 } description ? description : null,
            Instructions = Text(arguments, "instructions")?.Trim() ?? string.Empty,
            CookingTimeMinutes = Int(arguments, "cooking_time_minutes") ?? 0,
            Servings = Int(arguments, "servings") ?? 0,
            CategoryId = Int(arguments, "category_id") ?? 0
        };
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(recipe, new ValidationContext(recipe), errors, validateAllProperties: true))
            return new { error = string.Join(" ", errors.Select(item => item.ErrorMessage)) };
        if (!await _context.Categories.AnyAsync(item => item.Id == recipe.CategoryId))
            return new { error = "No category with this id in the kitchen. Call list_categories first." };
        if (await AddIngredientsAsync(recipe, arguments) is { } ingredientError) return new { error = ingredientError };

        _context.Recipes.Add(recipe);
        await _context.SaveChangesAsync();
        return new { created = true, recipe.Id, recipe.Title, Url = $"/Recipes/Details/{recipe.Id}" };
    }

    // Matches kitchen ingredients by name, case-insensitive, like the TheMealDB import; unknown names are created.
    private async Task<string?> AddIngredientsAsync(Recipe recipe, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("ingredients", out var items)
            || items.ValueKind != JsonValueKind.Array) return null;
        var lines = items.EnumerateArray().Select(item => (
            Name: Text(item, "name")?.Trim() ?? string.Empty,
            Quantity: Decimal(item, "quantity") ?? 0,
            Unit: Text(item, "unit")?.Trim() ?? string.Empty)).ToList();
        if (lines.Any(line => line.Name.Length is 0 or > 100))
            return "Each ingredient needs a name of 1-100 characters.";
        var keys = lines.Select(line => line.Name.ToLowerInvariant()).ToList();
        if (keys.Distinct().Count() != keys.Count) return "List each ingredient once.";

        var known = (await _context.Ingredients.Where(item => keys.Contains(item.Name.ToLower())).ToListAsync())
            .GroupBy(item => item.Name.ToLowerInvariant()).ToDictionary(group => group.Key, group => group.First());
        foreach (var line in lines)
        {
            var ingredient = known.GetValueOrDefault(line.Name.ToLowerInvariant()) ?? new Ingredient { Name = line.Name };
            var item = new RecipeIngredient { Ingredient = ingredient, Quantity = line.Quantity, Unit = line.Unit };
            var errors = new List<ValidationResult>();
            if (!Validator.TryValidateObject(item, new ValidationContext(item), errors, validateAllProperties: true))
                return $"{line.Name}: " + string.Join(" ", errors.Select(error => error.ErrorMessage));
            recipe.RecipeIngredients.Add(item);
        }
        return null;
    }

    private static decimal? Decimal(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? Int(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        // Models sometimes send numbers as strings.
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
