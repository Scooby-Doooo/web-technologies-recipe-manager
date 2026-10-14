namespace RecipeManager.Web.Features;

// Subscription plans of a kitchen. Which plan unlocks what is configured as feature flags in
// appsettings.json (FeatureManagement section), not hard-coded here.
public static class Plans
{
    public const string Free = "Free";
    public const string Premium = "Premium";

    public static readonly string[] All = [Free, Premium];
}

public static class FeatureFlags
{
    // The Premium-only page that exports a kitchen's recipes as JSON or CSV.
    public const string RecipeExport = "RecipeExport";

    // Without it a kitchen can hold at most Plans:FreeRecipeLimit recipes.
    public const string UnlimitedRecipes = "UnlimitedRecipes";

    // The AI assistant chat; every message costs money at OpenRouter.
    public const string AiAssistant = "AiAssistant";
}
