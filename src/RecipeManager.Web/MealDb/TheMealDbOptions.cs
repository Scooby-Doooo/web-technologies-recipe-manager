namespace RecipeManager.Web.MealDb;

// Bound from the "TheMealDb" section. "1" in the path is the free test key that TheMealDB
// gives for development and education, so it is not a secret. Tests point this at a fake server.
public class TheMealDbOptions
{
    public string ApiBaseUrl { get; set; } = "https://www.themealdb.com/api/json/v1/1/";
}
