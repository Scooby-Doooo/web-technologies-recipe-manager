namespace RecipeManager.Web.Telegram;

// Bound from the "Telegram" section. The bot is off without a token, so the app runs without it.
public class TelegramOptions
{
    public string? BotToken { get; set; }

    // Tests point this at a fake Telegram server.
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";

    // Public https address of /telegram/webhook. Empty means long polling, which works on localhost.
    public string? WebhookUrl { get; set; }

    // Telegram repeats it in the X-Telegram-Bot-Api-Secret-Token header of every webhook call.
    public string? WebhookSecret { get; set; }

    // Used for links in notifications, e.g. https://recipe-manager.example. Optional.
    public string? SiteUrl { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken);
}
