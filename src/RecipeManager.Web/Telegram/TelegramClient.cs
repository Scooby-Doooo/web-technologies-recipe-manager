using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RecipeManager.Web.Telegram;

// A small client for the Telegram Bot API (https://core.telegram.org/bots/api).
// The token is part of every URL, so this HttpClient is created by hand: clients from
// IHttpClientFactory log request URLs and would write the token into the logs.
public class TelegramClient
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly HttpClient _http;

    public TelegramClient(IOptions<TelegramOptions> options)
    {
        var settings = options.Value;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"{settings.ApiBaseUrl.TrimEnd('/')}/bot{settings.BotToken}/"),
            // Long polling waits up to 25 s on Telegram's side.
            Timeout = TimeSpan.FromSeconds(40)
        };
    }

    // Filled in at startup from getMe; the account page needs it for the t.me link.
    public string? BotUsername { get; private set; }

    public async Task<string> GetMeAsync(CancellationToken cancellationToken)
    {
        var me = await CallAsync("getMe", new { }, cancellationToken);
        BotUsername = me.GetProperty("username").GetString();
        return BotUsername!;
    }

    public Task<JsonElement> GetUpdatesAsync(long offset, CancellationToken cancellationToken) =>
        CallAsync("getUpdates", new { offset, timeout = 25, allowed_updates = new[] { "message" } }, cancellationToken);

    public Task SendMessageAsync(long chatId, string html, CancellationToken cancellationToken) =>
        CallAsync("sendMessage", new { chat_id = chatId, text = html, parse_mode = "HTML", link_preview_options = new { is_disabled = true } },
            cancellationToken);

    public Task SetWebhookAsync(string url, string? secret, CancellationToken cancellationToken) =>
        CallAsync("setWebhook", new { url, secret_token = secret, allowed_updates = new[] { "message" } }, cancellationToken);

    public Task DeleteWebhookAsync(CancellationToken cancellationToken) =>
        CallAsync("deleteWebhook", new { }, cancellationToken);

    private async Task<JsonElement> CallAsync(string method, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(method, body, Json, cancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!json.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var description = json.TryGetProperty("description", out var text) ? text.GetString() : null;
            throw new TelegramException($"Telegram {method} failed: {(int)response.StatusCode} {description}");
        }
        return json.GetProperty("result");
    }
}

public class TelegramException(string message) : Exception(message);
