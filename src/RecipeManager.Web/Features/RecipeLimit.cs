using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;
using RecipeManager.Web.Data;

namespace RecipeManager.Web.Features;

// The Free plan caps the number of recipes in a kitchen; UnlimitedRecipes lifts the cap.
public class RecipeLimit
{
    private readonly IFeatureManager _features;
    private readonly ApplicationDbContext _context;

    public RecipeLimit(IFeatureManager features, ApplicationDbContext context, IConfiguration configuration)
    {
        _features = features;
        _context = context;
        Limit = configuration.GetValue("Plans:FreeRecipeLimit", 20);
    }

    public int Limit { get; }

    public string Message => $"The Free plan allows {Limit} recipes per kitchen. Switch to Premium on your account page to add more.";

    public async Task<bool> CanAddRecipeAsync() =>
        await _features.IsEnabledAsync(FeatureFlags.UnlimitedRecipes)
        || await _context.Recipes.CountAsync() < Limit;
}
