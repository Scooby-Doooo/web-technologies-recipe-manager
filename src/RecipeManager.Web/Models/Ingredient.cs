using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace RecipeManager.Web.Models;

public class Ingredient : ITenantOwned
{
    public int Id { get; set; }

    // Set from the signed-in user on insert; forms and the API never bind it.
    [ValidateNever]
    public int TenantId { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [ValidateNever]
    public ICollection<RecipeIngredient> RecipeIngredients { get; set; } = new List<RecipeIngredient>();
}
