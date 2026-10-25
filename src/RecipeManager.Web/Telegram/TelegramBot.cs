using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Telegram;

// Answers one incoming Telegram message. Created in its own scope per update, so the kitchen
// it sets on TenantContext limits every query to the chat's kitchen.
public class TelegramBot
{
    // Telegram allows 4096 characters per message; long instructions are cut before escaping,
    // so the cut never splits an HTML entity.
    private const int MaxInstructionsLength = 2500;
    private readonly ApplicationDbContext _context;
    private readonly TenantContext _tenant;
    private readonly TelegramClient _client;

    public TelegramBot(ApplicationDbContext context, TenantContext tenant, TelegramClient client)
    {
        _context = context;
        _tenant = tenant;
        _client = client;
    }

    public async Task HandleUpdateAsync(JsonElement update, CancellationToken cancellationToken)
    {
        if (!update.TryGetProperty("message", out var message)
            || !message.TryGetProperty("text", out var textElement)
            || !message.TryGetProperty("chat", out var chatElement)) return;

        var chatId = chatElement.GetProperty("id").GetInt64();
        var text = textElement.GetString()?.Trim() ?? string.Empty;
        // "/recipe@MyBot soup" in groups: drop the bot name after the command.
        var space = text.IndexOf(' ');
        var command = (space < 0 ? text : text[..space]).Split('@')[0].ToLowerInvariant();
        var argument = space < 0 ? string.Empty : text[(space + 1)..].Trim();

        var reply = command == "/start" && argument.Length > 0
            ? await LinkAsync(chatId, ChatTitle(chatElement), argument)
            : await AnswerAsync(chatId, command, argument);
        await _client.SendMessageAsync(chatId, reply, cancellationToken);
    }

    private async Task<string> LinkAsync(long chatId, string title, string code)
    {
        var link = await _context.TelegramLinkCodes.FirstOrDefaultAsync(item => item.Code == code);
        if (link == null || link.ExpiresAt < DateTime.UtcNow)
            return "This link has expired. Open your Recipe Manager account page and press <b>Connect Telegram</b> again.";

        // A chat belongs to one kitchen; linking again moves it.
        var existing = await _context.TelegramChats.IgnoreQueryFilters().FirstOrDefaultAsync(item => item.ChatId == chatId);
        if (existing != null) _context.TelegramChats.Remove(existing);
        _context.TelegramLinkCodes.Remove(link);
        _context.TelegramChats.Add(new TelegramChat { ChatId = chatId, Title = title, TenantId = link.TenantId, LinkedAt = DateTime.UtcNow });
        _tenant.UseTenant(existing?.TenantId ?? link.TenantId); // the removal must happen inside the chat's old kitchen
        await _context.SaveChangesAsync();

        var kitchen = await _context.Tenants.AsNoTracking().Where(item => item.Id == link.TenantId).Select(item => item.Name).SingleAsync();
        return $"This chat is now linked to <b>{Html(kitchen)}</b>. You will get a message for every new recipe.\n\n{Help}";
    }

    private async Task<string> AnswerAsync(long chatId, string command, string argument)
    {
        var chat = await _context.TelegramChats.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(item => item.ChatId == chatId);
        if (chat == null)
            return "This chat is not linked to a kitchen yet. Open your Recipe Manager account page and press <b>Connect Telegram</b>.";
        _tenant.UseTenant(chat.TenantId);

        switch (command)
        {
            case "/recipes":
                var titles = await _context.Recipes.AsNoTracking().OrderBy(item => item.Title)
                    .Select(item => new { item.Id, item.Title, item.CookingTimeMinutes }).Take(30).ToListAsync();
                return titles.Count == 0
                    ? "Your kitchen has no recipes yet."
                    : "<b>Recipes</b>\n" + string.Join('\n', titles.Select(item => $"{item.Id}. {Html(item.Title)} — {item.CookingTimeMinutes} min"))
                      + "\n\nSend /recipe and a number or a name to read one.";
            case "/recipe":
                return await FindRecipeAsync(argument);
            case "/random":
                var count = await _context.Recipes.CountAsync();
                if (count == 0) return "Your kitchen has no recipes yet.";
                var randomId = await _context.Recipes.OrderBy(item => item.Id).Skip(Random.Shared.Next(count)).Select(item => item.Id).FirstAsync();
                return await RecipeTextAsync(randomId);
            case "/unlink":
                _context.TelegramChats.Remove(await _context.TelegramChats.SingleAsync(item => item.ChatId == chatId));
                await _context.SaveChangesAsync();
                return "This chat is no longer linked to the kitchen.";
            default:
                return Help;
        }
    }

    private async Task<string> FindRecipeAsync(string query)
    {
        if (query.Length == 0) return "Send /recipe and a number or a part of the name, for example <code>/recipe soup</code>.";
        if (int.TryParse(query, out var id))
            return await _context.Recipes.AnyAsync(item => item.Id == id) ? await RecipeTextAsync(id) : "No recipe with this number in your kitchen.";

        var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var matches = await _context.Recipes.AsNoTracking()
            .Where(item => EF.Functions.ILike(item.Title, pattern, "\\"))
            .OrderBy(item => item.Title).Select(item => new { item.Id, item.Title }).Take(10).ToListAsync();
        return matches.Count switch
        {
            0 => "No recipe matches this name.",
            1 => await RecipeTextAsync(matches[0].Id),
            _ => "Several recipes match:\n" + string.Join('\n', matches.Select(item => $"{item.Id}. {Html(item.Title)}"))
                 + "\n\nSend /recipe and the number."
        };
    }

    private async Task<string> RecipeTextAsync(int id)
    {
        var recipe = await _context.Recipes.AsNoTracking()
            .Include(item => item.Category)
            .Include(item => item.RecipeIngredients).ThenInclude(item => item.Ingredient)
            .SingleAsync(item => item.Id == id);
        var text = new StringBuilder($"<b>{Html(recipe.Title)}</b>\n{Html(recipe.Category.Name)} · {recipe.CookingTimeMinutes} min · {recipe.Servings} servings\n");
        if (!string.IsNullOrWhiteSpace(recipe.Description)) text.Append('\n').Append(Html(recipe.Description)).Append('\n');
        if (recipe.RecipeIngredients.Count > 0)
            text.Append("\n<b>Ingredients</b>\n").AppendJoin('\n', recipe.RecipeIngredients.Select(item =>
                $"• {Html(item.Ingredient.Name)} — {item.Quantity:0.##} {Html(item.Unit)}")).Append('\n');
        var instructions = recipe.Instructions.Length <= MaxInstructionsLength
            ? recipe.Instructions
            : recipe.Instructions[..MaxInstructionsLength] + "…";
        text.Append("\n<b>Instructions</b>\n").Append(Html(instructions));
        return text.ToString();
    }

    private const string Help = "Commands:\n/recipes — list the kitchen's recipes\n/recipe &lt;number or name&gt; — read a recipe\n/random — a random recipe\n/unlink — stop linking this chat";

    // Telegram's HTML mode needs only &, < and > escaped; user text must never become markup.
    public static string Html(string value) => WebUtility.HtmlEncode(value);

    private static string ChatTitle(JsonElement chat)
    {
        foreach (var name in new[] { "title", "username", "first_name" })
            if (chat.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text)
                return text.Length > 200 ? text[..200] : text;
        return "Telegram chat";
    }
}
