using System.ComponentModel.DataAnnotations;

namespace RecipeManager.Web.Models;

// A kitchen: one tenant of the app. Recipes, categories and ingredients belong to exactly one
// kitchen, and its members never see the data of other kitchens.
public class Tenant
{
    // The seeded kitchen that anonymous visitors browse; its recipes are the public landing pages.
    public const int PublicId = 1;

    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    [Display(Name = "Kitchen name")]
    public string Name { get; set; } = string.Empty;

    // Lets another user join this kitchen. The public kitchen has none, so nobody can join it this way.
    [StringLength(16)]
    public string? InviteCode { get; set; }

    public DateTime CreatedAt { get; set; }

    // Free or Premium; feature flags decide what each plan unlocks.
    [Required]
    [StringLength(20)]
    public string Plan { get; set; } = Features.Plans.Free;
}
