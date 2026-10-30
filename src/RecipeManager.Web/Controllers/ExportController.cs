using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement.Mvc;
using RecipeManager.Web.Data;
using RecipeManager.Web.Features;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Controllers;

// Premium only: on Free every action returns the "Premium feature" page (403).
[Authorize]
[FeatureGate(FeatureFlags.RecipeExport)]
public class ExportController : Controller
{
    private readonly ApplicationDbContext _context;

    public ExportController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index()
    {
        ViewData["RecipeCount"] = await _context.Recipes.CountAsync();
        return View();
    }

    public async Task<IActionResult> Json()
    {
        var recipes = (await LoadRecipes()).Select(recipe => new
        {
            recipe.Id,
            recipe.Title,
            Category = recipe.Category.Name,
            recipe.Description,
            recipe.Instructions,
            recipe.CookingTimeMinutes,
            recipe.Servings,
            Ingredients = recipe.RecipeIngredients.Select(item => new { item.Ingredient.Name, item.Quantity, item.Unit })
        });
        var json = JsonSerializer.SerializeToUtf8Bytes(recipes, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        return File(json, "application/json", FileName("json"));
    }

    public async Task<IActionResult> Csv()
    {
        var csv = new StringBuilder("Id,Title,Category,CookingTimeMinutes,Servings,Description\r\n");
        foreach (var recipe in await LoadRecipes())
        {
            csv.AppendJoin(',', recipe.Id.ToString(CultureInfo.InvariantCulture), CsvField(recipe.Title), CsvField(recipe.Category.Name),
                recipe.CookingTimeMinutes.ToString(CultureInfo.InvariantCulture), recipe.Servings.ToString(CultureInfo.InvariantCulture),
                CsvField(recipe.Description ?? string.Empty)).Append("\r\n");
        }
        // The BOM lets Excel open the Ukrainian text as UTF-8.
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", FileName("csv"));
    }

    private Task<List<Recipe>> LoadRecipes() => _context.Recipes.AsNoTracking()
        .Include(recipe => recipe.Category)
        .Include(recipe => recipe.RecipeIngredients).ThenInclude(item => item.Ingredient)
        .OrderBy(recipe => recipe.Title).ThenBy(recipe => recipe.Id)
        .ToListAsync();

    private static string FileName(string extension) => $"recipes-{DateTime.UtcNow:yyyy-MM-dd}.{extension}";

    // Quotes every field, and neutralises a leading = + - @ so a spreadsheet does not run it as a formula.
    private static string CsvField(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
