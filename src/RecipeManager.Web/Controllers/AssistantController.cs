using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.FeatureManagement.Mvc;
using RecipeManager.Web.Assistant;
using RecipeManager.Web.Features;

namespace RecipeManager.Web.Controllers;

// The AI assistant chat. Every model call costs money, so it is a Premium feature.
[Authorize]
[FeatureGate(FeatureFlags.AiAssistant)]
public class AssistantController : Controller
{
    private const int MaxMessages = 20;
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);
    private readonly OpenRouterClient _client;

    public AssistantController(OpenRouterClient client)
    {
        _client = client;
    }

    public IActionResult Index()
    {
        ViewData["AssistantConfigured"] = _client.Configured;
        return View();
    }

    // Streams the answer as Server-Sent Events: reasoning, tool_call, tool_result and content
    // events while the agent works, then done (or error). The page reads them with fetch,
    // because EventSource can only send GET requests without a body.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task Chat([FromBody] ChatRequest? request, [FromServices] AssistantAgent agent)
    {
        var problem = Validate(request);
        if (problem != null || !_client.Configured)
        {
            Response.StatusCode = problem != null ? StatusCodes.Status400BadRequest : StatusCodes.Status503ServiceUnavailable;
            await Response.WriteAsJsonAsync(new { error = problem ?? "The assistant is not configured on this server." });
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // keeps reverse proxies from holding the stream back

        async Task Emit(string type, object data)
        {
            await Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(data, EventJson)}\n\n", HttpContext.RequestAborted);
            await Response.Body.FlushAsync(HttpContext.RequestAborted);
        }

        try
        {
            await agent.RunAsync(request!.Messages!.Select(item => new AssistantMessage(item.Role, item.Content)), Emit, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            // The user pressed Stop or closed the page.
        }
        catch (Exception exception) when (exception is OpenRouterException or HttpRequestException or TaskCanceledException)
        {
            await Emit("error", new { message = exception is OpenRouterException ? exception.Message : "The AI service did not answer. Try again." });
        }
    }

    // The browser keeps the conversation. Only user and assistant turns are accepted, so a
    // client cannot slip in its own system prompt or fake tool results.
    private static string? Validate(ChatRequest? request)
    {
        // A body that is not JSON of this shape binds to null.
        if (request?.Messages is not { Count: > 0 and <= MaxMessages }) return $"Send from 1 to {MaxMessages} messages.";
        if (request.Messages.Any(item => item.Role is not ("user" or "assistant"))) return "Only user and assistant messages are allowed.";
        if (request.Messages[^1].Role != "user") return "The last message must come from the user.";
        var errors = new List<ValidationResult>();
        return request.Messages.All(item => Validator.TryValidateObject(item, new ValidationContext(item), errors, true))
            ? null : errors[0].ErrorMessage;
    }

    public class ChatRequest
    {
        public List<ChatMessage>? Messages { get; set; }
    }

    public class ChatMessage
    {
        [Required]
        public string Role { get; set; } = string.Empty;

        [Required(AllowEmptyStrings = false)]
        [StringLength(4000, ErrorMessage = "A message can have at most 4000 characters.")]
        public string Content { get; set; } = string.Empty;
    }
}
