namespace RecipeManager.Web.Assistant;

// Bound from the "OpenRouter" section. The key lives in user secrets or environment variables.
public class OpenRouterOptions
{
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "deepseek/deepseek-v4.1-flash-1017";

    // OpenRouter speaks the OpenAI chat completions protocol; tests point this at a fake server.
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    // Caps the cost of one model call.
    public int MaxTokens { get; set; } = 2000;

    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey);
}
