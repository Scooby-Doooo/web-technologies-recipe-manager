using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;
using RecipeManager.Web.Models.Api;

namespace RecipeManager.Web.Controllers.Api;

[ApiController]
[Route("api/ingredients")]
[AutoValidateAntiforgeryToken]
public class IngredientsApiController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public IngredientsApiController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<List<LookupResponse>>> GetAll()
    {
        return await _context.Ingredients.AsNoTracking().OrderBy(item => item.Name)
            .Select(item => new LookupResponse(item.Id, item.Name)).ToListAsync();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LookupResponse>> GetById(int id)
    {
        var item = await _context.Ingredients.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id);
        return item == null ? NotFound() : Ok(new LookupResponse(item.Id, item.Name));
    }

    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<LookupResponse>> Create([FromBody] LookupRequest request)
    {
        var item = new Ingredient { Name = request.Name };
        _context.Ingredients.Add(item);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Problem("Unable to save the ingredient.", statusCode: 500);
        }
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, new LookupResponse(item.Id, item.Name));
    }

    [HttpPut("{id:int}")]
    [HttpPost("{id:int}")]
    [Consumes("application/json")]
    public async Task<ActionResult<LookupResponse>> Update(int id, [FromBody] LookupRequest request)
    {
        var item = await _context.Ingredients.FindAsync(id);
        if (item == null) return NotFound();
        item.Name = request.Name;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Problem("Unable to save the ingredient.", statusCode: 500);
        }
        return Ok(new LookupResponse(item.Id, item.Name));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var item = await _context.Ingredients.FindAsync(id);
        if (item == null) return NotFound();
        if (await _context.RecipeIngredients.AnyAsync(item => item.IngredientId == id))
            return Conflict(new ProblemDetails { Status = 409, Title = "This ingredient is in use and cannot be deleted." });

        _context.Ingredients.Remove(item);
        try
        {
            await _context.SaveChangesAsync();
        }
        // SQLite 787 is SQLITE_CONSTRAINT_FOREIGNKEY; a reference may appear after the check.
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 787 })
        {
            return Conflict(new ProblemDetails { Status = 409, Title = "This ingredient is in use and cannot be deleted." });
        }
        catch (DbUpdateException)
        {
            return Problem("Unable to delete the ingredient.", statusCode: 500);
        }
        return NoContent();
    }
}
