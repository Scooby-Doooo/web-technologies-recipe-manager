using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;
using RecipeManager.Web.Models.Api;

namespace RecipeManager.Web.Controllers.Api;

[ApiController]
[Route("api/ingredients")]
[AutoValidateAntiforgeryToken]
[Authorize]
public class IngredientsApiController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public IngredientsApiController(ApplicationDbContext context)
    {
        _context = context;
    }

    [AllowAnonymous]
    [HttpGet]
    [OutputCache(PolicyName = OutputCacheInvalidationInterceptor.IngredientsTag)]
    public async Task<IActionResult> GetAll(
        [FromQuery, Range(0, int.MaxValue)] int skip = 0,
        [FromQuery, Range(1, 100)] int limit = 20)
    {
        var query = _context.Ingredients.AsNoTracking().OrderBy(item => item.Name).ThenBy(item => item.Id)
            .Select(item => new LookupResponse(item.Id, item.Name));
        // Keep the C5 array contract when pagination is not requested.
        if (!Request.Query.ContainsKey("skip") && !Request.Query.ContainsKey("limit"))
            return Ok(await query.ToListAsync());

        var items = await query.Skip(skip).Take(limit + 1).ToListAsync();
        var hasNextPage = items.Count > limit;
        if (hasNextPage) items.RemoveAt(items.Count - 1);
        var nextLink = hasNextPage
            ? Url.Action(nameof(GetAll), new { skip = (long)skip + limit, limit })
            : null;
        return Ok(new PageResponse<LookupResponse>(items, skip, limit, nextLink));
    }

    [AllowAnonymous]
    [HttpGet("{id:int}")]
    [OutputCache(PolicyName = OutputCacheInvalidationInterceptor.IngredientsTag)]
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
        // A reference may appear after the check; PostgreSQL then reports a foreign key violation.
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
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
