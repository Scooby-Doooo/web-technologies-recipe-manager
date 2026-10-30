using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;

namespace RecipeManager.Mcp;

public record Category(int Id, string Name);

public record RecipeIngredient(int IngredientId, string Name, decimal Quantity, string Unit);

public record Recipe(int Id, string Title, string? Description, string Instructions,
    int CookingTimeMinutes, int Servings, int CategoryId, string CategoryName, string? ImagePath,
    IReadOnlyList<RecipeIngredient> Ingredients);

public record Page<T>(IReadOnlyList<T> Items, int Skip, int Limit, string? NextLink);

// Client for the Recipe Manager JSON API; the MCP server never touches the database.
public class RecipeApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private (string Header, string Token)? _antiforgery;

    public RecipeApiClient(HttpClient http)
    {
        _http = http;
    }

    public Task<Recipe> GetRecipe(int id, CancellationToken cancellationToken) =>
        Send<Recipe>(new HttpRequestMessage(HttpMethod.Get, $"api/recipes/{id}"), cancellationToken);

    public Task<Page<T>> GetPage<T>(string relativeUrl, CancellationToken cancellationToken) =>
        Send<Page<T>>(new HttpRequestMessage(HttpMethod.Get, relativeUrl.TrimStart('/')), cancellationToken);

    public async Task<T> Post<T>(string relativeUrl, object body, CancellationToken cancellationToken)
    {
        var (header, token) = _antiforgery ??= await GetAntiforgeryToken(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Post, relativeUrl) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Add(header, token);
        return await Send<T>(request, cancellationToken);
    }

    private async Task<(string, string)> GetAntiforgeryToken(CancellationToken cancellationToken)
    {
        // The cookie is kept by the handler's CookieContainer; the token goes into a header.
        var data = await Send<JsonElement>(new HttpRequestMessage(HttpMethod.Get, "api/antiforgery"), cancellationToken);
        return (data.GetProperty("headerName").GetString()!, data.GetProperty("requestToken").GetString()!);
    }

    private async Task<T> Send<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new McpException($"Recipe Manager API at {_http.BaseAddress} is not reachable: {exception.Message}");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return (await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken))!;
            // API errors become tool errors with the API's message, so the caller can fix the input.
            throw new McpException(await DescribeError(response, cancellationToken));
        }
    }

    private static async Task<string> DescribeError(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = $"API returned {(int)response.StatusCode} {response.StatusCode}";
        if (response.StatusCode == HttpStatusCode.NotFound) return text + ": record not found.";
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return text + ": sign in to Recipe Manager, create an API token on the account page and set RECIPE_API_TOKEN.";
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken);
            if (problem.TryGetProperty("errors", out var errors))
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(field => field.Value.EnumerateArray().Select(message => $"{field.Name}: {message.GetString()}"));
                return text + ": " + string.Join("; ", messages);
            }
            if (problem.TryGetProperty("detail", out var detail)) return text + ": " + detail.GetString();
            if (problem.TryGetProperty("title", out var title)) return text + ": " + title.GetString();
        }
        catch (JsonException)
        {
        }
        return text + ".";
    }
}
