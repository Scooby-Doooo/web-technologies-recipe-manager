using System.ComponentModel.DataAnnotations;

namespace RecipeManager.Web.Models.Api;

public class LookupRequest
{
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;
}

public record LookupResponse(int Id, string Name);

// Only editable fields are accepted; IDs, image paths and navigation properties are not bound.
public class RecipeRequest
{
    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required]
    [StringLength(10000)]
    public string Instructions { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int CookingTimeMinutes { get; set; }

    [Range(1, int.MaxValue)]
    public int Servings { get; set; }

    [Range(1, int.MaxValue)]
    public int CategoryId { get; set; }
}

public record RecipeIngredientResponse(int IngredientId, string Name, decimal Quantity, string Unit);

public record RecipeResponse(int Id, string Title, string? Description, string Instructions,
    int CookingTimeMinutes, int Servings, int CategoryId, string CategoryName, string? ImagePath,
    IReadOnlyList<RecipeIngredientResponse> Ingredients);
