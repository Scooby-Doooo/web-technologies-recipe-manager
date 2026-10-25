using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;
using RecipeManager.Web.Photos;

namespace RecipeManager.Web.MealDb;

// Turns a TheMealDB meal into a recipe of the current kitchen. Category and ingredients are
// matched by name with what the kitchen already has, and only missing ones are created.
public partial class MealImporter
{
    // TheMealDB has no cooking time or servings; the user corrects them right after the import.
    public const int DefaultCookingTimeMinutes = 30;
    public const int DefaultServings = 4;
    private static readonly Dictionary<char, decimal> VulgarFractions = new()
    {
        ['¼'] = 0.25m, ['½'] = 0.5m, ['¾'] = 0.75m, ['⅓'] = 1m / 3, ['⅔'] = 2m / 3, ['⅛'] = 0.125m
    };
    private readonly ApplicationDbContext _context;
    private readonly TheMealDbClient _client;
    private readonly IPhotoStorage _photos;

    public MealImporter(ApplicationDbContext context, TheMealDbClient client, IPhotoStorage photos)
    {
        _context = context;
        _client = client;
        _photos = photos;
    }

    // Returns null when TheMealDB has no meal with this id.
    public async Task<Recipe?> ImportAsync(int mealId, CancellationToken cancellationToken)
    {
        var meal = await _client.LookupAsync(mealId, cancellationToken);
        if (meal == null) return null;

        var categoryName = Cut(meal.Category ?? "Imported", 100);
        var category = await _context.Categories
            .FirstOrDefaultAsync(item => item.Name.ToLower() == categoryName.ToLower(), cancellationToken)
            ?? new Category { Name = categoryName };
        var recipe = new Recipe
        {
            Title = Cut(meal.Name, 200),
            Description = meal.Area == null ? "Imported from TheMealDB." : $"{meal.Area} cuisine. Imported from TheMealDB.",
            Instructions = Cut(meal.Instructions.ReplaceLineEndings("\n").Trim() is { Length: > 0 } text ? text : "See the original recipe on TheMealDB.", 10000),
            CookingTimeMinutes = DefaultCookingTimeMinutes,
            Servings = DefaultServings,
            Category = category,
            MealDbId = meal.Id
        };

        var names = meal.Ingredients.Select(item => Cut(item.Name, 100).ToLowerInvariant()).Distinct().ToList();
        var known = (await _context.Ingredients.Where(item => names.Contains(item.Name.ToLower())).ToListAsync(cancellationToken))
            .GroupBy(item => item.Name.ToLowerInvariant()).ToDictionary(group => group.Key, group => group.First());
        var added = new Dictionary<string, RecipeIngredient>();
        foreach (var item in meal.Ingredients)
        {
            var name = Cut(item.Name, 100);
            var key = name.ToLowerInvariant();
            var (quantity, unit) = ParseMeasure(item.Measure);
            // A recipe holds an ingredient once; a repeat with the same unit adds up, any other is skipped.
            if (added.TryGetValue(key, out var existing))
            {
                if (string.Equals(existing.Unit, unit, StringComparison.OrdinalIgnoreCase))
                    existing.Quantity = Math.Min(existing.Quantity + quantity, 1_000_000m);
                continue;
            }
            if (!known.TryGetValue(key, out var ingredient))
                known[key] = ingredient = new Ingredient { Name = name };
            var line = new RecipeIngredient { Ingredient = ingredient, Quantity = quantity, Unit = unit };
            added[key] = line;
            recipe.RecipeIngredients.Add(line);
        }

        recipe.ImagePath = await SaveImageAsync(meal.Thumbnail, cancellationToken);
        _context.Recipes.Add(recipe);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _photos.DeleteAsync(recipe.ImagePath);
            throw;
        }
        return recipe;
    }

    // "1 1/2 cups" → 1.5 cups, "200g" → 200 g, "¾ cup" → 0.75 cup, "2-3 cloves" → 2 cloves,
    // "To taste" → 1 To taste. An empty measure means one piece.
    public static (decimal Quantity, string Unit) ParseMeasure(string? measure)
    {
        var text = (measure ?? string.Empty).Trim();
        decimal? quantity = null;
        var fraction = FractionPattern().Match(text);
        if (fraction.Success)
        {
            var whole = fraction.Groups["whole"].Success ? decimal.Parse(fraction.Groups["whole"].Value, CultureInfo.InvariantCulture) : 0;
            quantity = whole + decimal.Parse(fraction.Groups["top"].Value, CultureInfo.InvariantCulture)
                / decimal.Parse(fraction.Groups["bottom"].Value, CultureInfo.InvariantCulture);
            text = text[fraction.Length..];
        }
        else
        {
            var number = NumberPattern().Match(text);
            if (number.Groups["number"].Success || number.Groups["vulgar"].Success)
            {
                quantity = (number.Groups["number"].Success
                               ? decimal.Parse(number.Groups["number"].Value.Replace(',', '.'), CultureInfo.InvariantCulture)
                               : 0)
                           + (number.Groups["vulgar"].Success ? VulgarFractions[number.Groups["vulgar"].Value[0]] : 0);
                text = text[number.Length..];
            }
        }
        if (quantity != null) text = RangeEndPattern().Replace(text, string.Empty);
        var unit = Cut(text.Trim(), 30);
        if (unit.Length == 0) unit = "pcs";
        return (Math.Clamp(Math.Round(quantity ?? 1, 3), 0.001m, 1_000_000m), unit);
    }

    private async Task<string?> SaveImageAsync(string? url, CancellationToken cancellationToken)
    {
        var image = await _client.DownloadImageAsync(url, cancellationToken);
        if (image == null) return null;
        try
        {
            return await _photos.SaveAsync(new MemoryStream(image.Value.Content), image.Value.Extension, cancellationToken);
        }
        catch (PhotoStorageException)
        {
            // A recipe without a photo is still worth importing.
            return null;
        }
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd();

    [GeneratedRegex(@"^(?:(?<whole>\d+)\s+)?(?<top>\d+)\s*/\s*(?<bottom>[1-9]\d*)")]
    private static partial Regex FractionPattern();

    [GeneratedRegex(@"^(?<number>\d+(?:[.,]\d+)?)?\s*(?<vulgar>[¼½¾⅓⅔⅛])?")]
    private static partial Regex NumberPattern();

    // The upper end of a range such as "2-3" or "2 to 3".
    [GeneratedRegex(@"^\s*(?:-|–|to\s)\s*\d+(?:[.,]\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex RangeEndPattern();
}
