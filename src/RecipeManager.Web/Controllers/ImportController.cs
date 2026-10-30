using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Features;
using RecipeManager.Web.MealDb;

namespace RecipeManager.Web.Controllers;

// Search on TheMealDB and import into the current kitchen.
[Authorize]
public class ImportController : Controller
{
    private const int MaximumResults = 48;
    private readonly ApplicationDbContext _context;
    private readonly TheMealDbClient _mealDb;

    public ImportController(ApplicationDbContext context, TheMealDbClient mealDb)
    {
        _context = context;
        _mealDb = mealDb;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        try
        {
            ViewData["Categories"] = await _mealDb.CategoriesAsync(cancellationToken);
            ViewData["Ingredients"] = await _mealDb.IngredientsAsync(cancellationToken);
        }
        catch (MealDbException exception)
        {
            ViewData["Error"] = exception.Message;
        }
        return View();
    }

    // Name search, category and main ingredient can be combined; at least one is needed.
    [HttpGet]
    public async Task<IActionResult> Search(string? term, string? category, string? ingredient, CancellationToken cancellationToken)
    {
        term = term?.Trim() ?? string.Empty;
        category = category?.Trim() ?? string.Empty;
        ingredient = ingredient?.Trim() ?? string.Empty;
        foreach (var (name, value) in new[] { (nameof(term), term), (nameof(category), category), (nameof(ingredient), ingredient) })
        {
            if (value.Length > 100) ModelState.AddModelError(name, "Search values must be 100 characters or fewer.");
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (term.Length < 2 && category.Length == 0 && ingredient.Length == 0)
            return Json(new { total = 0, meals = Array.Empty<object>() });

        List<MealSummary> found;
        try
        {
            if (term.Length >= 2)
            {
                found = (await _mealDb.SearchAsync(term, cancellationToken))
                    .Where(meal => category.Length == 0 || string.Equals(meal.Category, category, StringComparison.OrdinalIgnoreCase))
                    .Where(meal => ingredient.Length == 0
                                   || meal.Ingredients.Any(item => string.Equals(item.Name, ingredient, StringComparison.OrdinalIgnoreCase)))
                    .Select(meal => meal.Summary).ToList();
            }
            else
            {
                // filter.php takes one value, so two filters are two calls and their intersection.
                var byCategory = category.Length > 0 ? await _mealDb.FilterByCategoryAsync(category, cancellationToken) : null;
                var byIngredient = ingredient.Length > 0 ? await _mealDb.FilterByIngredientAsync(ingredient, cancellationToken) : null;
                found = byCategory != null && byIngredient != null
                    ? byCategory.IntersectBy(byIngredient.Select(meal => meal.Id), meal => meal.Id).ToList()
                    : (byCategory ?? byIngredient)!.ToList();
            }
        }
        catch (MealDbException exception)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = exception.Message });
        }

        var page = found.OrderBy(meal => meal.Name, StringComparer.OrdinalIgnoreCase).Take(MaximumResults).ToList();
        var imported = await ImportedRecipesAsync(page.Select(meal => meal.Id).ToList(), cancellationToken);
        return Json(new
        {
            total = found.Count,
            meals = page.Select(meal => new
            {
                id = meal.Id, name = meal.Name, thumbnail = meal.Thumbnail,
                recipeId = imported.TryGetValue(meal.Id, out var recipeId) ? recipeId : (int?)null
            })
        });
    }

    [HttpGet]
    public async Task<IActionResult> Meal(int id, CancellationToken cancellationToken)
    {
        try
        {
            var meal = await _mealDb.LookupAsync(id, cancellationToken);
            if (meal == null) return NotFound(new { error = "TheMealDB has no such meal." });
            var imported = await ImportedRecipesAsync([meal.Id], cancellationToken);
            return Json(new
            {
                meal.Id, meal.Name, meal.Category, meal.Area, meal.Instructions, meal.Thumbnail, meal.Youtube,
                ingredients = meal.Ingredients.Select(item => new { item.Name, item.Measure }),
                recipeId = imported.TryGetValue(meal.Id, out var recipeId) ? recipeId : (int?)null
            });
        }
        catch (MealDbException exception)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = exception.Message });
        }
    }

    [HttpPost, ActionName("Meal")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportMeal(int id, [FromServices] MealImporter importer, [FromServices] RecipeLimit limit,
        CancellationToken cancellationToken)
    {
        var existing = await ImportedRecipesAsync([id], cancellationToken);
        if (existing.TryGetValue(id, out var existingId)) return AlreadyImported(existingId);
        if (!await limit.CanAddRecipeAsync())
        {
            ViewData["Reason"] = limit.Message;
            var result = View("PremiumRequired");
            result.StatusCode = StatusCodes.Status403Forbidden;
            return result;
        }

        try
        {
            var recipe = await importer.ImportAsync(id, cancellationToken);
            if (recipe == null) return NotFound();
            TempData["RecipeMessage"] = "The recipe is imported from TheMealDB. It has no cooking time or servings there, " +
                                        $"so {MealImporter.DefaultCookingTimeMinutes} minutes and {MealImporter.DefaultServings} servings are set. Check them and save.";
            return RedirectToAction("Edit", "Recipes", new { id = recipe.Id });
        }
        catch (MealDbException exception)
        {
            TempData["ImportError"] = exception.Message;
            return RedirectToAction(nameof(Index));
        }
        catch (DbUpdateException)
        {
            // Two imports of the same meal at once: the unique index lets only one of them in.
            _context.ChangeTracker.Clear();
            existing = await ImportedRecipesAsync([id], cancellationToken);
            if (existing.TryGetValue(id, out existingId)) return AlreadyImported(existingId);
            TempData["ImportError"] = "Unable to save the recipe. Please try again.";
            return RedirectToAction(nameof(Index));
        }
    }

    private IActionResult AlreadyImported(int recipeId)
    {
        TempData["RecipeMessage"] = "This meal is already in your kitchen.";
        return RedirectToAction("Details", "Recipes", new { id = recipeId });
    }

    private async Task<Dictionary<int, int>> ImportedRecipesAsync(List<int> mealIds, CancellationToken cancellationToken) =>
        await _context.Recipes.AsNoTracking()
            .Where(recipe => recipe.MealDbId != null && mealIds.Contains(recipe.MealDbId.Value))
            .ToDictionaryAsync(recipe => recipe.MealDbId!.Value, recipe => recipe.Id, cancellationToken);
}
