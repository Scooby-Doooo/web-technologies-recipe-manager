using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RecipeManager.Web.Data;

namespace RecipeManager.Web.Telegram;

// Runs the bot: receives messages by long polling (or leaves that to the webhook endpoint when
// Telegram:WebhookUrl is set) and sends the queued notifications about new recipes.
public class TelegramService : BackgroundService
{
    private readonly TelegramClient _client;
    private readonly IServiceScopeFactory _scopes;
    private readonly Channel<RecipeCreated> _queue;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(TelegramClient client, IServiceScopeFactory scopes, Channel<RecipeCreated> queue,
        IOptions<TelegramOptions> options, ILogger<TelegramService> logger)
    {
        _client = client;
        _scopes = scopes;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Telegram bot @{Bot} is running", await _client.GetMeAsync(stoppingToken));
        }
        catch (Exception exception) when (exception is TelegramException or HttpRequestException)
        {
            // A wrong token must not stop the web app; the bot just stays off.
            _logger.LogError("Telegram bot is off: {Reason}", exception.Message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            try
            {
                await _client.SetWebhookAsync(_options.WebhookUrl, _options.WebhookSecret, stoppingToken);
            }
            catch (Exception exception) when (exception is TelegramException or HttpRequestException)
            {
                // Same for a bad webhook address: no incoming messages, but announcements still go out.
                _logger.LogError("Telegram webhook is not set: {Reason}", exception.Message);
            }
            await SendNotificationsAsync(stoppingToken);
        }
        else
        {
            // getUpdates does not work while a webhook is set, e.g. one left from a deployment.
            await _client.DeleteWebhookAsync(stoppingToken);
            await Task.WhenAll(PollAsync(stoppingToken), SendNotificationsAsync(stoppingToken));
        }
    }

    private async Task PollAsync(CancellationToken stoppingToken)
    {
        long offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var update in (await _client.GetUpdatesAsync(offset, stoppingToken)).EnumerateArray())
                {
                    offset = update.GetProperty("update_id").GetInt64() + 1;
                    await HandleUpdateAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _logger.LogWarning("Telegram polling failed, retrying: {Reason}", exception.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
            }
        }
    }

    // Also used by the webhook endpoint. One failing message must not stop the bot.
    public async Task HandleUpdateAsync(JsonElement update, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TelegramBot>().HandleUpdateAsync(update, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Telegram update could not be handled: {Reason}", exception.Message);
        }
    }

    private async Task SendNotificationsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var created in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await NotifyAsync(created, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning("Telegram notification for recipe {Id} failed: {Reason}", created.RecipeId, exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    // Also used by the RabbitMQ consumer.
    public async Task NotifyAsync(RecipeCreated created, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().UseTenant(created.TenantId);
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var chats = await context.TelegramChats.AsNoTracking().Select(chat => chat.ChatId).ToListAsync(cancellationToken);
        if (chats.Count == 0) return;
        var recipe = await context.Recipes.AsNoTracking().Include(item => item.Category)
            .FirstOrDefaultAsync(item => item.Id == created.RecipeId, cancellationToken);
        if (recipe == null) return; // deleted before the message went out

        var text = $"New recipe: <b>{TelegramBot.Html(recipe.Title)}</b>\n{TelegramBot.Html(recipe.Category.Name)} · {recipe.CookingTimeMinutes} min"
            + (string.IsNullOrWhiteSpace(_options.SiteUrl) ? string.Empty : $"\n{TelegramBot.Html(_options.SiteUrl.TrimEnd('/'))}/Recipes/Details/{recipe.Id}")
            + $"\n\nSend /recipe {recipe.Id} to read it here.";
        foreach (var chatId in chats) await _client.SendMessageAsync(chatId, text, cancellationToken);
    }
}
