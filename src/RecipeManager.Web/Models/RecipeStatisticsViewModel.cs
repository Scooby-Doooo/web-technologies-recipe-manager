namespace RecipeManager.Web.Models;

public record StatisticCount(string Label, int Count);

public record RecipeStatisticsViewModel(
    IReadOnlyList<StatisticCount> Categories,
    IReadOnlyList<StatisticCount> CookingTimes)
{
    public int TotalRecipes => CookingTimes.Sum(item => item.Count);
}
