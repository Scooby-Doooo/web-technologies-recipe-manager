using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Controllers;

public class StatisticsController : Controller
{
    private readonly ApplicationDbContext _context;

    public StatisticsController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index()
    {
        // One grouped SQL query supplies both charts without loading full recipes.
        var counts = await _context.Recipes.AsNoTracking()
            .GroupBy(recipe => new
            {
                recipe.CategoryId,
                CategoryName = recipe.Category.Name,
                TimeGroup = recipe.CookingTimeMinutes <= 15 ? 0
                    : recipe.CookingTimeMinutes <= 30 ? 1
                    : recipe.CookingTimeMinutes <= 60 ? 2 : 3
            })
            .Select(group => new
            {
                group.Key.CategoryId,
                group.Key.CategoryName,
                group.Key.TimeGroup,
                Count = group.Count()
            })
            .ToListAsync();

        var categories = counts.GroupBy(item => new { item.CategoryId, item.CategoryName })
            .OrderBy(group => group.Key.CategoryName).ThenBy(group => group.Key.CategoryId)
            .Select(group => new StatisticCount(group.Key.CategoryName, group.Sum(item => item.Count)))
            .ToList();
        string[] timeLabels = ["1–15 min", "16–30 min", "31–60 min", "Over 60 min"];
        var cookingTimes = timeLabels.Select((label, index) =>
            new StatisticCount(label, counts.Where(item => item.TimeGroup == index).Sum(item => item.Count)))
            .ToList();

        return View(new RecipeStatisticsViewModel(categories, cookingTimes));
    }
}
