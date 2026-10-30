using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Features;
using RecipeManager.Web.Models;
using RecipeManager.Web.Photos;

namespace RecipeManager.Web.Controllers;

// Anyone can browse; creating, editing and deleting requires signing in.
[Authorize]
public class RecipesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IPhotoStorage _photos;
    private const long MaximumImageSize = 5 * 1024 * 1024;
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public RecipesController(ApplicationDbContext context, IPhotoStorage photos)
    {
        _context = context;
        _photos = photos;
    }

    private ViewResult LimitReached(RecipeLimit limit)
    {
        ViewData["Reason"] = limit.Message;
        var result = View("PremiumRequired");
        result.StatusCode = StatusCodes.Status403Forbidden;
        return result;
    }

    [AllowAnonymous]
    public async Task<IActionResult> Index()
    {
        return View(await _context.Recipes.AsNoTracking()
            .Include(recipe => recipe.Category)
            .OrderBy(recipe => recipe.Title).ToListAsync());
    }

    [AllowAnonymous]
    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();
        var recipe = await _context.Recipes.AsNoTracking()
            .Include(recipe => recipe.Category)
            .Include(recipe => recipe.RecipeIngredients)
                .ThenInclude(item => item.Ingredient)
            .FirstOrDefaultAsync(recipe => recipe.Id == id);
        if (recipe == null) return NotFound();
        return View(recipe);
    }

    public async Task<IActionResult> Create([FromServices] RecipeLimit limit)
    {
        if (!await limit.CanAddRecipeAsync()) return LimitReached(limit);
        await PopulateCategories();
        return View(new Recipe { CookingTimeMinutes = 30, Servings = 2 });
    }

    [HttpGet]
    public async Task<IActionResult> CategoryOptions(string? term, int page = 1,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 20;
        term = term?.Trim() ?? string.Empty;
        if (term.Length > 100)
            ModelState.AddModelError(nameof(term), "Search must be 100 characters or fewer.");
        if (page < 1 || page > int.MaxValue / pageSize)
            ModelState.AddModelError(nameof(page), "Invalid result page.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (term.Length < 3)
            return Json(new { results = Array.Empty<object>(), pagination = new { more = false } });

        // Escape the LIKE wildcards so the input is matched as plain text.
        var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var matches = await _context.Categories.AsNoTracking()
            .Where(category => EF.Functions.ILike(category.Name, pattern, "\\"))
            .OrderBy(category => category.Name).ThenBy(category => category.Id)
            .Skip((page - 1) * pageSize).Take(pageSize + 1)
            .Select(category => new { id = category.Id, text = category.Name })
            .ToListAsync(cancellationToken);
        return Json(new { results = matches.Take(pageSize), pagination = new { more = matches.Count > pageSize } });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        [Bind("Title,Description,Instructions,CookingTimeMinutes,Servings,CategoryId")] Recipe recipe,
        IFormFile? imageFile, [FromServices] RecipeLimit limit)
    {
        if (!await limit.CanAddRecipeAsync()) return LimitReached(limit);
        if (!await _context.Categories.AnyAsync(category => category.Id == recipe.CategoryId))
            ModelState.AddModelError(nameof(Recipe.CategoryId), "Please select an existing category.");

        ValidateImage(imageFile);
        if (ModelState.IsValid)
        {
            recipe.ImagePath = imageFile == null ? null : await SaveImage(imageFile);
            if (ModelState.IsValid)
            {
                _context.Recipes.Add(recipe);
                try
                {
                    await _context.SaveChangesAsync();
                    // Read once by the Index view, which reports the creation to Google Analytics.
                    TempData["CreatedRecipeId"] = recipe.Id;
                    return RedirectToAction(nameof(Index));
                }
                catch (DbUpdateException)
                {
                    await _photos.DeleteAsync(recipe.ImagePath);
                    recipe.ImagePath = null;
                    ModelState.AddModelError(string.Empty, "Unable to save the recipe. Please try again.");
                }
            }
        }

        await PopulateCategories(recipe.CategoryId);
        return View(recipe);
    }

    // Notes editor page.
    public async Task<IActionResult> Notes(int? id)
    {
        if (id == null) return NotFound();
        var recipe = await _context.Recipes.AsNoTracking().FirstOrDefaultAsync(recipe => recipe.Id == id);
        if (recipe == null) return NotFound();
        return View(recipe);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();
        var recipe = await _context.Recipes.FindAsync(id);
        if (recipe == null) return NotFound();
        await PopulateCategories(recipe.CategoryId);
        return View(recipe);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id,
        [Bind("Id,Title,Description,Instructions,CookingTimeMinutes,Servings,CategoryId")] Recipe form,
        IFormFile? imageFile)
    {
        if (id != form.Id) return NotFound();
        var recipe = await _context.Recipes.FindAsync(id);
        if (recipe == null) return NotFound();

        if (!await _context.Categories.AnyAsync(category => category.Id == form.CategoryId))
            ModelState.AddModelError(nameof(Recipe.CategoryId), "Please select an existing category.");

        var oldImagePath = recipe.ImagePath;
        // Read the current path from the database, never from posted form values.
        form.ImagePath = oldImagePath;
        ValidateImage(imageFile);
        if (ModelState.IsValid)
        {
            var newImagePath = imageFile == null ? null : await SaveImage(imageFile);
            if (!ModelState.IsValid)
            {
                await PopulateCategories(form.CategoryId);
                return View(form);
            }
            recipe.Title = form.Title;
            recipe.Description = form.Description;
            recipe.Instructions = form.Instructions;
            recipe.CookingTimeMinutes = form.CookingTimeMinutes;
            recipe.Servings = form.Servings;
            recipe.CategoryId = form.CategoryId;
            recipe.ImagePath = newImagePath ?? oldImagePath;
            try
            {
                await _context.SaveChangesAsync();
                if (newImagePath != null) await _photos.DeleteAsync(oldImagePath);
                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateException)
            {
                await _photos.DeleteAsync(newImagePath);
                ModelState.AddModelError(string.Empty, "Unable to save the recipe. Please try again.");
            }
        }

        await PopulateCategories(form.CategoryId);
        return View(form);
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();
        var recipe = await _context.Recipes.AsNoTracking()
            .Include(recipe => recipe.Category)
            .FirstOrDefaultAsync(recipe => recipe.Id == id);
        if (recipe == null) return NotFound();
        return View(recipe);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var recipe = await _context.Recipes.FindAsync(id);
        if (recipe == null) return NotFound();
        _context.Recipes.Remove(recipe);
        await _context.SaveChangesAsync();
        await _photos.DeleteAsync(recipe.ImagePath);
        return RedirectToAction(nameof(Index));
    }

    private void ValidateImage(IFormFile? imageFile)
    {
        if (imageFile == null) return;
        var extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
        if (!ImageExtensions.Contains(extension))
            ModelState.AddModelError("imageFile", "Choose a JPG, JPEG, PNG or WEBP image.");
        if (imageFile.Length == 0 || imageFile.Length > MaximumImageSize)
            ModelState.AddModelError("imageFile", "The image must not be empty and must be 5 MB or smaller.");
    }

    private async Task<string?> SaveImage(IFormFile imageFile)
    {
        try
        {
            await using var stream = imageFile.OpenReadStream();
            return await _photos.SaveAsync(stream, Path.GetExtension(imageFile.FileName).ToLowerInvariant());
        }
        catch (PhotoStorageException)
        {
            ModelState.AddModelError("imageFile", "Unable to save the image. Please try again.");
            return null;
        }
    }

    private async Task PopulateCategories(int? selectedCategory = null)
    {
        var categories = await _context.Categories.AsNoTracking()
            .Where(category => category.Id == selectedCategory)
            .Select(category => new { category.Id, category.Name }).ToListAsync();
        ViewBag.CategoryId = new SelectList(categories, "Id", "Name", selectedCategory);
    }
}
