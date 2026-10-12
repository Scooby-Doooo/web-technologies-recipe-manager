namespace RecipeManager.Web.Models;

public class AccountViewModel
{
    public required AppUser User { get; init; }
    public required Tenant Kitchen { get; init; }
    public required IReadOnlyList<AppUser> Members { get; init; }
    public required IReadOnlyList<TelegramChat> TelegramChats { get; init; }

    // Null when the bot is off (no token) or has not reached Telegram yet.
    public string? TelegramBotUsername { get; init; }
}
