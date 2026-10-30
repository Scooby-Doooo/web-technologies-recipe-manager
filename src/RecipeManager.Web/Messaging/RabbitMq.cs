using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RecipeManager.Web.Telegram;

namespace RecipeManager.Web.Messaging;

// Bound from the "RabbitMq" section. Without Url the in-memory queue is used.
public class RabbitMqOptions
{
    // amqp://user:password@host:5672/vhost or amqps://… for a hosted broker (CloudAMQP).
    public string? Url { get; set; }

    // How long a failed announcement waits in the retry queue before the next attempt.
    public int RetryDelayMilliseconds { get; set; } = 30_000;

    // Attempts before a message is parked in the failed queue for a person to look at.
    public int MaxAttempts { get; set; } = 5;

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);
}

// One connection per process, opened on first use; the client reconnects by itself after a drop.
// Every user declares the same durable topology, so the first one to start creates it:
//
//   exchange recipe-manager --recipe.created--> telegram-notifications --(consumer)--> Telegram
//          failed attempt: nack --> telegram-notifications.retry (TTL) --> telegram-notifications
//          after MaxAttempts:   --> telegram-notifications.failed
public sealed class RabbitMqBus(IOptions<RabbitMqOptions> options, ILogger<RabbitMqBus> logger) : IAsyncDisposable
{
    public const string Exchange = "recipe-manager";
    public const string RecipeCreatedKey = "recipe.created";
    public const string Queue = "telegram-notifications";
    public const string RetryQueue = Queue + ".retry";
    public const string FailedQueue = Queue + ".failed";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public RabbitMqOptions Options { get; } = options.Value;

    public async Task<IChannel> OpenChannelAsync(bool confirmPublishes, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection == null)
            {
                var factory = new ConnectionFactory { Uri = new Uri(Options.Url!), ClientProvidedName = "recipe-manager" };
                _connection = await factory.CreateConnectionAsync(cancellationToken);
                logger.LogInformation("Connected to RabbitMQ");
            }
        }
        finally
        {
            _lock.Release();
        }
        var channel = await _connection.CreateChannelAsync(new CreateChannelOptions(
            publisherConfirmationsEnabled: confirmPublishes, publisherConfirmationTrackingEnabled: confirmPublishes),
            cancellationToken);
        await DeclareAsync(channel, cancellationToken);
        return channel;
    }

    private async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);
        // A rejected message goes through the default exchange to the retry queue, waits there for
        // RetryDelay and expires back into the main queue.
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = RetryQueue
            }, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(RetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = Options.RetryDelayMilliseconds,
                ["x-dead-letter-exchange"] = "",
                ["x-dead-letter-routing-key"] = Queue
            }, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(FailedQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(Queue, Exchange, RecipeCreatedKey, cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null) await _connection.DisposeAsync();
    }
}

// Publishes with publisher confirms: the save waits until the broker has stored the message.
public sealed class RabbitMqRecipeCreatedPublisher(RabbitMqBus bus, ILogger<RabbitMqRecipeCreatedPublisher> logger)
    : IRecipeCreatedPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(RecipeCreated created)
    {
        // The recipe is already saved: if the broker is unreachable, the announcement is lost and
        // logged, but the save is not turned into an error.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _lock.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Unable to publish recipe {Id} to RabbitMQ: timed out", created.RecipeId);
            return;
        }
        try
        {
            if (_channel is not { IsOpen: true })
            {
                if (_channel != null) await _channel.DisposeAsync();
                _channel = await bus.OpenChannelAsync(confirmPublishes: true, timeout.Token);
            }
            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = Guid.NewGuid().ToString("N"),
                Type = RabbitMqBus.RecipeCreatedKey
            };
            await _channel.BasicPublishAsync(RabbitMqBus.Exchange, RabbitMqBus.RecipeCreatedKey, mandatory: true,
                properties, JsonSerializer.SerializeToUtf8Bytes(created), timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogError(exception, "Unable to publish recipe {Id} to RabbitMQ", created.RecipeId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel != null) await _channel.DisposeAsync();
    }
}

// Reads telegram-notifications and sends each announcement. A message is acknowledged only after
// Telegram accepted it, so a crash or restart in between delivers it again (at least once).
public sealed class TelegramQueueConsumer(RabbitMqBus bus, TelegramService telegram, ILogger<TelegramQueueConsumer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The broker may be starting together with the app: keep trying until it answers.
        IChannel? channel = null;
        while (channel == null && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                channel = await bus.OpenChannelAsync(confirmPublishes: false, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("RabbitMQ is not reachable, retrying: {Reason}", exception.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ContinueWith(_ => { });
            }
        }
        if (channel == null) return;

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 10, global: false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, message) => HandleAsync(channel, message, stoppingToken);
        await channel.BasicConsumeAsync(RabbitMqBus.Queue, autoAck: false, consumer, stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        await channel.DisposeAsync();
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs message, CancellationToken stoppingToken)
    {
        RecipeCreated? created = null;
        try
        {
            created = JsonSerializer.Deserialize<RecipeCreated>(message.Body.Span);
            if (created == null) throw new JsonException("Empty message.");
            await telegram.NotifyAsync(created, stoppingToken);
            await channel.BasicAckAsync(message.DeliveryTag, multiple: false, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Not acknowledged: the broker hands it out again after the restart.
        }
        catch (Exception exception)
        {
            var attempt = PreviousAttempts(message.BasicProperties) + 1;
            if (created == null || attempt >= bus.Options.MaxAttempts)
            {
                // Park it with its original body and headers, then drop it from the main queue.
                logger.LogError("Announcement {MessageId} failed {Attempt} times, moved to {Queue}: {Reason}",
                    message.BasicProperties.MessageId, attempt, RabbitMqBus.FailedQueue, exception.Message);
                await channel.BasicPublishAsync("", RabbitMqBus.FailedQueue, mandatory: false,
                    new BasicProperties(message.BasicProperties), message.Body, CancellationToken.None);
                await channel.BasicAckAsync(message.DeliveryTag, multiple: false, CancellationToken.None);
            }
            else
            {
                logger.LogWarning("Announcement {MessageId} failed (attempt {Attempt}), retrying: {Reason}",
                    message.BasicProperties.MessageId, attempt, exception.Message);
                await channel.BasicNackAsync(message.DeliveryTag, multiple: false, requeue: false, CancellationToken.None);
            }
        }
    }

    // RabbitMQ counts dead-lettering in the x-death header: one entry per queue and reason.
    private static long PreviousAttempts(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue("x-death", out var value) != true || value is not IList<object> deaths) return 0;
        return deaths.OfType<IDictionary<string, object?>>()
            .Where(death => death.TryGetValue("queue", out var queue) && queue is byte[] name
                && Encoding.UTF8.GetString(name) == RabbitMqBus.Queue)
            .Sum(death => death.TryGetValue("count", out var count) && count is long number ? number : 0);
    }
}
