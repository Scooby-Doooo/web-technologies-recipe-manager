using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace RecipeManager.Web.Models;

public class Recipe : ITenantOwned
{
    public int Id { get; set; }

    // Set from the signed-in user on insert; forms and the API never bind it.
    [ValidateNever]
    public int TenantId { get; set; }

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required]
    [StringLength(10000)]
    public string Instructions { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    [Display(Name = "Cooking time (minutes)")]
    public int CookingTimeMinutes { get; set; }

    [Range(1, int.MaxValue)]
    public int Servings { get; set; }

    public string? ImagePath { get; set; }

    // Id of the meal on TheMealDB for imported recipes (C8), so a meal is imported once per kitchen.
    [ValidateNever]
    public int? MealDbId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Please select a category.")]
    [Display(Name = "Category")]
    public int CategoryId { get; set; }

    // The form posts CategoryId, so MVC must not validate the unloaded navigation.
    [ValidateNever]
    public Category Category { get; set; } = null!;

    [ValidateNever]
    public ICollection<RecipeIngredient> RecipeIngredients { get; set; } = new List<RecipeIngredient>();
}
