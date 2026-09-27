using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace RecipeManager.Web.Models;

public class RecipeIngredient
{
    public int RecipeId { get; set; }

    [ValidateNever]
    public Recipe Recipe { get; set; } = null!;

    public int IngredientId { get; set; }

    [ValidateNever]
    public Ingredient Ingredient { get; set; } = null!;

    [Range(typeof(decimal), "0.001", "1000000")]
    public decimal Quantity { get; set; }

    [Required]
    [StringLength(30)]
    public string Unit { get; set; } = string.Empty;
}
