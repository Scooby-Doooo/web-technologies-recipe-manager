using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace RecipeManager.Web.Models;

public class Recipe
{
    public int Id { get; set; }

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

    [Range(1, int.MaxValue, ErrorMessage = "Please select a category.")]
    [Display(Name = "Category")]
    public int CategoryId { get; set; }

    // The form posts CategoryId, so MVC must not validate the unloaded navigation.
    [ValidateNever]
    public Category Category { get; set; } = null!;

    [ValidateNever]
    public ICollection<RecipeIngredient> RecipeIngredients { get; set; } = new List<RecipeIngredient>();
}
