using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace RecipeManager.Web.Assistant;

// Streams chat completions from OpenRouter (https://openrouter.ai/docs/api-reference/streaming).
// Each yielded element is one chunk: a delta with text, reasoning or a piece of a tool call.
public class OpenRouterClient
{
    private readonly HttpClient _http;
    private readonly OpenRouterOptions _options;

    public OpenRouterClient(HttpClient http, IOptions<OpenRouterOptions> options)
    {
        _options = options.Value;
        _http = http;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(2);
    }

    public bool Configured => _options.Configured;

    public async IAsyncEnumerable<JsonElement> StreamAsync(JsonArray messages, JsonArray tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["stream"] = true,
            ["max_tokens"] = _options.MaxTokens,
            ["messages"] = messages.DeepClone(),
            ["tools"] = tools.DeepClone(),
            // Ask for the model's reasoning so the page can show it; "low" keeps it short and cheap.
            ["reasoning"] = new JsonObject { ["effort"] = "low" }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Add("X-Title", "Recipe Manager"); // shown in the OpenRouter activity log

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new OpenRouterException($"OpenRouter returned {(int)response.StatusCode}: {(error.Length > 300 ? error[..300] : error)}");
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            // Lines like ": OPENROUTER PROCESSING" are keep-alive comments of the SSE protocol.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            using var chunk = JsonDocument.Parse(data);
            if (chunk.RootElement.TryGetProperty("error", out var streamError))
                throw new OpenRouterException("OpenRouter stopped the answer: "
                    + (streamError.TryGetProperty("message", out var message) ? message.GetString() : "unknown error"));
            yield return chunk.RootElement.Clone();
        }
    }
}

public class OpenRouterException(string message) : Exception(message);
