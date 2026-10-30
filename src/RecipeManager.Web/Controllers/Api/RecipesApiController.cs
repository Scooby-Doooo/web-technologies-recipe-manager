using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Features;
using RecipeManager.Web.Models;
using RecipeManager.Web.Models.Api;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using RecipeManager.Web.Photos;
using RecipeManager.Web.Search;

namespace RecipeManager.Web.Controllers.Api;

[ApiController]
[Route("api/recipes")]
[AutoValidateAntiforgeryToken]
[Authorize]
public class RecipesApiController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IPhotoStorage _photos;
    private const long MaximumImageSize = 5 * 1024 * 1024;
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public RecipesApiController(ApplicationDbContext context, IPhotoStorage photos)
    {
        _context = context;
        _photos = photos;
    }

    [AllowAnonymous]
    [HttpGet]
    [OutputCache(PolicyName = OutputCacheInvalidationInterceptor.RecipesTag)]
    public async Task<IActionResult> GetAll(
        [FromQuery, Range(0, int.MaxValue)] int skip = 0,
        [FromQuery, Range(1, 100)] int limit = 20)
    {
        var query = RecipeQuery().OrderBy(recipe => recipe.Title).ThenBy(recipe => recipe.Id);
        // Keep the C5 array contract when pagination is not requested.
        if (!Request.Query.ContainsKey("skip") && !Request.Query.ContainsKey("limit"))
            return Ok((await query.ToListAsync()).Select(ToResponse).ToList());

        var recipes = await query.Skip(skip).Take(limit + 1).ToListAsync();
        var hasNextPage = recipes.Count > limit;
        if (hasNextPage) recipes.RemoveAt(recipes.Count - 1);
        var nextLink = hasNextPage
            ? Url.Action(nameof(GetAll), new { skip = (long)skip + limit, limit })
            : null;
        return Ok(new PageResponse<RecipeResponse>(recipes.Select(ToResponse).ToList(), skip, limit, nextLink));
    }

    // Full-text search with Lucene query syntax, best match first. Words are joined with AND.
    [AllowAnonymous]
    [HttpGet("search")]
    [OutputCache(PolicyName = OutputCacheInvalidationInterceptor.RecipesTag)]
    public async Task<IActionResult> Search(
        [FromServices] RecipeSearchIndex index,
        [FromServices] TenantContext tenant,
        [FromQuery, Required, StringLength(200)] string q,
        [FromQuery, Range(1, int.MaxValue)] int? maxMinutes = null,
        [FromQuery, Range(0, 1000)] int skip = 0,
        [FromQuery, Range(1, 100)] int limit = 20)
    {
        SearchResult result;
        try
        {
            result = await index.SearchAsync(_context, tenant.TenantId, q, maxMinutes, skip, limit, HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is ParseException or BooleanQuery.TooManyClausesException)
        {
            ModelState.AddModelError(nameof(q), "The search query is not valid: " + exception.Message);
            return ValidationProblem(ModelState);
        }
        var nextLink = skip + limit < result.Total
            ? Url.Action(nameof(Search), new { q, maxMinutes, skip = skip + limit, limit })
            : null;
        return Ok(new SearchResponse(q, result.Total, result.Hits, skip, limit, nextLink));
    }

    [AllowAnonymous]
    [HttpGet("{id:int}")]
    [OutputCache(PolicyName = OutputCacheInvalidationInterceptor.RecipesTag)]
    public async Task<ActionResult<RecipeResponse>> GetById(int id)
    {
        var recipe = await RecipeQuery().FirstOrDefaultAsync(recipe => recipe.Id == id);
        return recipe == null ? NotFound() : Ok(ToResponse(recipe));
    }

    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<RecipeResponse>> Create([FromBody] RecipeRequest request, [FromServices] RecipeLimit limit)
    {
        if (!await limit.CanAddRecipeAsync())
            return Problem(title: limit.Message, statusCode: StatusCodes.Status403Forbidden);
        if (!await _context.Categories.AnyAsync(category => category.Id == request.CategoryId))
        {
            ModelState.AddModelError(nameof(request.CategoryId), "Please select an existing category.");
            return ValidationProblem(ModelState);
        }
        var recipe = new Recipe();
        ApplyRequest(recipe, request);
        _context.Recipes.Add(recipe);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Problem("Unable to save the recipe.", statusCode: 500);
        }
        var saved = await RecipeQuery().FirstAsync(item => item.Id == recipe.Id);
        return CreatedAtAction(nameof(GetById), new { id = recipe.Id }, ToResponse(saved));
    }

    [HttpPut("{id:int}")]
    [HttpPost("{id:int}")]
    [Consumes("application/json")]
    public async Task<ActionResult<RecipeResponse>> Update(int id, [FromBody] RecipeRequest request)
    {
        var recipe = await _context.Recipes.FindAsync(id);
        if (recipe == null) return NotFound();
        if (!await _context.Categories.AnyAsync(category => category.Id == request.CategoryId))
        {
            ModelState.AddModelError(nameof(request.CategoryId), "Please select an existing category.");
            return ValidationProblem(ModelState);
        }
        ApplyRequest(recipe, request);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Problem("Unable to save the recipe.", statusCode: 500);
        }
        return Ok(ToResponse(await RecipeQuery().FirstAsync(item => item.Id == id)));
    }

    [HttpPost("{id:int}/image")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumImageSize + 1024 * 1024)]
    public async Task<ActionResult<RecipeResponse>> UploadImage(int id, [FromForm] IFormFile? imageFile)
    {
        var recipe = await _context.Recipes.FindAsync(id);
        if (recipe == null) return NotFound();
        var extension = Path.GetExtension(imageFile?.FileName ?? "").ToLowerInvariant();
        if (imageFile == null || imageFile.Length == 0 || imageFile.Length > MaximumImageSize ||
            !ImageExtensions.Contains(extension))
        {
            ModelState.AddModelError(nameof(imageFile), "Choose a JPG, JPEG, PNG or WEBP image up to 5 MB.");
            return ValidationProblem(ModelState);
        }

        string imagePath;
        try
        {
            await using var stream = imageFile.OpenReadStream();
            imagePath = await _photos.SaveAsync(stream, extension);
        }
        catch (PhotoStorageException)
        {
            return Problem("Unable to save the recipe image.", statusCode: 500);
        }

        var oldImagePath = recipe.ImagePath;
        recipe.ImagePath = imagePath;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            await _photos.DeleteAsync(imagePath);
            return Problem("Unable to save the recipe.", statusCode: 500);
        }
        await _photos.DeleteAsync(oldImagePath);
        return Ok(ToResponse(await RecipeQuery().FirstAsync(item => item.Id == id)));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var recipe = await _context.Recipes.FindAsync(id);
        if (recipe == null) return NotFound();
        _context.Recipes.Remove(recipe);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Problem("Unable to delete the recipe.", statusCode: 500);
        }
        await _photos.DeleteAsync(recipe.ImagePath);
        return NoContent();
    }

    private IQueryable<Recipe> RecipeQuery() => _context.Recipes.AsNoTracking()
        .Include(recipe => recipe.Category)
        .Include(recipe => recipe.RecipeIngredients).ThenInclude(item => item.Ingredient);

    private static RecipeResponse ToResponse(Recipe recipe) => new(recipe.Id, recipe.Title,
        recipe.Description, recipe.Instructions, recipe.CookingTimeMinutes, recipe.Servings,
        recipe.CategoryId, recipe.Category.Name, recipe.ImagePath,
        recipe.RecipeIngredients.OrderBy(item => item.Ingredient.Name)
            .Select(item => new RecipeIngredientResponse(item.IngredientId, item.Ingredient.Name,
                item.Quantity, item.Unit)).ToList());

    private static void ApplyRequest(Recipe recipe, RecipeRequest request)
    {
        recipe.Title = request.Title;
        recipe.Description = request.Description;
        recipe.Instructions = request.Instructions;
        recipe.CookingTimeMinutes = request.CookingTimeMinutes;
        recipe.Servings = request.Servings;
        recipe.CategoryId = request.CategoryId;
    }
}
