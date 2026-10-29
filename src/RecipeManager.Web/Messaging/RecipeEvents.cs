using System.Threading.Channels;
using RecipeManager.Web.Telegram;

namespace RecipeManager.Web.Messaging;

// Where "a recipe was created" goes after the save. The consumer sends the Telegram announcement.
public interface IRecipeCreatedPublisher
{
    Task PublishAsync(RecipeCreated created);
}

// Without RabbitMQ: an in-memory queue read by TelegramService. Messages are lost on restart and a
// failed announcement is not retried, which is enough for development and tests.
public class ChannelRecipeCreatedPublisher(Channel<RecipeCreated> queue) : IRecipeCreatedPublisher
{
    public Task PublishAsync(RecipeCreated created)
    {
        queue.Writer.TryWrite(created);
        return Task.CompletedTask;
    }
}
