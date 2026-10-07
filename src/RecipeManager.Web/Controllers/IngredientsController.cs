using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Controllers;

// Anyone can browse; creating, editing and deleting requires signing in.
[Authorize]
public class IngredientsController : Controller
{
    private readonly ApplicationDbContext _context;

    public IngredientsController(ApplicationDbContext context)
    {
        _context = context;
    }

    [AllowAnonymous]
    public async Task<IActionResult> Index()
    {
        return View(await _context.Ingredients.AsNoTracking().OrderBy(item => item.Name).ToListAsync());
    }

    [AllowAnonymous]
    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();
        var item = await _context.Ingredients.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    public IActionResult Create()
    {
        return View(new Ingredient());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Name")] Ingredient item)
    {
        if (!ModelState.IsValid) return View(item);
        _context.Ingredients.Add(item);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();
        var item = await _context.Ingredients.FindAsync(id);
        if (item == null) return NotFound();
        return View(item);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,Name")] Ingredient form)
    {
        if (id != form.Id) return NotFound();
        var item = await _context.Ingredients.FindAsync(id);
        if (item == null) return NotFound();
        if (!ModelState.IsValid) return View(form);

        item.Name = form.Name;
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();
        var item = await _context.Ingredients.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
        if (item == null) return NotFound();
        return View(item);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var item = await _context.Ingredients.FindAsync(id);
        if (item == null) return NotFound();

        if (await _context.RecipeIngredients.AnyAsync(item => item.IngredientId == id))
        {
            ModelState.AddModelError(string.Empty, "This ingredient is used by a recipe and cannot be deleted.");
            return View("Delete", item);
        }

        _context.Ingredients.Remove(item);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A recipe may have started using this item after the check above.
            ModelState.AddModelError(string.Empty, "Unable to delete this ingredient. It may still be used by a recipe.");
            return View("Delete", item);
        }

        return RedirectToAction(nameof(Index));
    }
}
