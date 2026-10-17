using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace RecipeManager.Web.MealDb;

public record MealSummary(int Id, string Name, string? Thumbnail);

public record MealIngredient(string Name, string Measure);

public record Meal(int Id, string Name, string? Category, string? Area, string Instructions,
    string? Thumbnail, string? Youtube, IReadOnlyList<MealIngredient> Ingredients)
{
    public MealSummary Summary => new(Id, Name, Thumbnail);
}

public class MealDbException(string message, Exception? inner = null) : Exception(message, inner);

// Reads TheMealDB (https://www.themealdb.com/api.php). Answers are cached in memory, because
// the same lists and searches repeat while a user types and TheMealDB data rarely changes.
public class TheMealDbClient
{
    public const long MaximumImageSize = 5 * 1024 * 1024;
    private static readonly Dictionary<string, string> ImageTypes = new()
    {
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/webp"] = ".webp"
    };
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;

    public TheMealDbClient(HttpClient http, IOptions<TheMealDbOptions> options, IMemoryCache cache)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Value.ApiBaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
        _cache = cache;
    }

    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken cancellationToken) =>
        (await GetMealsAsync("list.php?c=list", TimeSpan.FromDays(1), cancellationToken))
            .Select(item => Text(item, "strCategory")).OfType<string>().ToList();

    public async Task<IReadOnlyList<string>> IngredientsAsync(CancellationToken cancellationToken) =>
        (await GetMealsAsync("list.php?i=list", TimeSpan.FromDays(1), cancellationToken))
            .Select(item => Text(item, "strIngredient")).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();

    // Search by name returns whole meals, so category and ingredient can be checked without more calls.
    public async Task<IReadOnlyList<Meal>> SearchAsync(string name, CancellationToken cancellationToken) =>
        (await GetMealsAsync("search.php?s=" + Uri.EscapeDataString(name), TimeSpan.FromHours(1), cancellationToken))
            .Select(ToMeal).ToList();

    // filter.php accepts one value at a time and returns only id, name and picture.
    public async Task<IReadOnlyList<MealSummary>> FilterByCategoryAsync(string category, CancellationToken cancellationToken) =>
        await FilterAsync("c", category, cancellationToken);

    public async Task<IReadOnlyList<MealSummary>> FilterByIngredientAsync(string ingredient, CancellationToken cancellationToken) =>
        await FilterAsync("i", ingredient, cancellationToken);

    public async Task<Meal?> LookupAsync(int id, CancellationToken cancellationToken) =>
        (await GetMealsAsync("lookup.php?i=" + id, TimeSpan.FromHours(1), cancellationToken)).Select(ToMeal).FirstOrDefault();

    // Only pictures from the TheMealDB host itself are downloaded, so a changed answer cannot make
    // the server fetch an arbitrary address. Returns null when the picture is missing or unsuitable.
    public async Task<(byte[] Content, string Extension)?> DownloadImageAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || !SameServer(address)) return null;
        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            // HttpClient follows redirects, so the final address is checked as well.
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri is not { } final || !SameServer(final)
                || !ImageTypes.TryGetValue(type, out var extension)
                || response.Content.Headers.ContentLength > MaximumImageSize)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                // Content-Length can be missing, so the size is also checked while reading.
                if (buffer.Length + read > MaximumImageSize) return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.Length == 0 ? null : (buffer.ToArray(), extension);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private bool SameServer(Uri address) => Uri.Compare(address, _http.BaseAddress, UriComponents.SchemeAndServer,
        UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private async Task<IReadOnlyList<MealSummary>> FilterAsync(string parameter, string value, CancellationToken cancellationToken) =>
        (await GetMealsAsync($"filter.php?{parameter}={Uri.EscapeDataString(value)}", TimeSpan.FromHours(1), cancellationToken))
            .Select(item => new MealSummary(Id(item), Text(item, "strMeal") ?? string.Empty, Text(item, "strMealThumb")))
            .Where(meal => meal.Id > 0).ToList();

    // TheMealDB answers {"meals": [...]} and {"meals": null} when nothing is found.
    private async Task<JsonElement[]> GetMealsAsync(string path, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(path, out JsonElement[]? cached)) return cached!;
        JsonElement answer;
        try
        {
            answer = await _http.GetFromJsonAsync<JsonElement>(path, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException
                                          || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new MealDbException("TheMealDB is not available right now. Please try again later.", exception);
        }
        var meals = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("meals", out var list)
                    && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToArray()
            : [];
        _cache.Set(path, meals, duration);
        return meals;
    }

    private static Meal ToMeal(JsonElement item)
    {
        // A meal has up to 20 numbered ingredient and measure fields; unused ones are empty or null.
        var ingredients = new List<MealIngredient>();
        for (var number = 1; number <= 20; number++)
        {
            var name = Text(item, "strIngredient" + number);
            if (!string.IsNullOrWhiteSpace(name))
                ingredients.Add(new MealIngredient(name.Trim(), Text(item, "strMeasure" + number)?.Trim() ?? string.Empty));
        }
        return new Meal(Id(item), Text(item, "strMeal") ?? string.Empty, Text(item, "strCategory"),
            Text(item, "strArea") ?? Text(item, "strCountry"), Text(item, "strInstructions") ?? string.Empty,
            Text(item, "strMealThumb"), Text(item, "strYoutube"), ingredients);
    }

    private static int Id(JsonElement item) => int.TryParse(Text(item, "idMeal"), out var id) ? id : 0;

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
