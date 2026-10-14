using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RecipeManager.Web.Assistant;

public record AssistantMessage(string Role, string Content);

// The agent loop: stream the model's answer; when it asks for tools, run them, add the results
// to the conversation and ask again. Every step is reported through `emit` as it happens,
// which the controller turns into Server-Sent Events.
public class AssistantAgent
{
    private const int MaxToolRounds = 5;

    private const string SystemPrompt = """
        You are the cooking assistant of Recipe Manager, a web app where a kitchen keeps its recipes.
        Answer in the language the user writes in. Use the tools to look at the user's kitchen instead
        of guessing what recipes it has, and say so when the kitchen has nothing that fits.
        Category names are usually English and broad (Breakfast, Main Course, Dessert, Salad), while
        recipe titles may be in any language. To find a kind of dish, such as soups, search by a word
        of the title in the user's language and, if needed, list all recipes.
        Save a recipe with create_recipe only when the user asks you to add or save one; call
        list_categories first to choose the category id, and tell the user what you saved.
        Keep answers short and practical.
        """;

    private readonly OpenRouterClient _client;
    private readonly AssistantTools _tools;

    public AssistantAgent(OpenRouterClient client, AssistantTools tools)
    {
        _client = client;
        _tools = tools;
    }

    public async Task RunAsync(IEnumerable<AssistantMessage> history, Func<string, object, Task> emit, CancellationToken cancellationToken)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt } };
        foreach (var message in history)
            messages.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content });

        for (var round = 0; round < MaxToolRounds; round++)
        {
            var content = new StringBuilder();
            var calls = new SortedDictionary<int, ToolCall>();
            await foreach (var chunk in _client.StreamAsync(messages, AssistantTools.Definitions, cancellationToken))
            {
                if (!chunk.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0
                    || !choices[0].TryGetProperty("delta", out var delta)) continue;

                if (Text(delta, "reasoning") is { Length: > 0 } reasoning)
                    await emit("reasoning", new { text = reasoning });
                if (Text(delta, "content") is { Length: > 0 } text)
                {
                    content.Append(text);
                    await emit("content", new { text });
                }
                // A tool call arrives in pieces: id and name first, then the arguments bit by bit.
                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var piece in toolCalls.EnumerateArray())
                    {
                        var index = piece.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : 0;
                        var call = calls.TryGetValue(index, out var existing) ? existing : calls[index] = new ToolCall();
                        if (Text(piece, "id") is { Length: > 0 } id) call.Id = id;
                        if (piece.TryGetProperty("function", out var function))
                        {
                            if (Text(function, "name") is { Length: > 0 } name) call.Name += name;
                            if (Text(function, "arguments") is { } arguments) call.Arguments.Append(arguments);
                        }
                    }
                }
            }

            if (calls.Count == 0)
            {
                await emit("done", new { });
                return;
            }
            // The tool result must point at its call; some providers leave the id out.
            foreach (var (index, call) in calls)
                if (call.Id.Length == 0) call.Id = $"call_{round}_{index}";

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content.Length > 0 ? content.ToString() : null,
                ["tool_calls"] = new JsonArray(calls.Values.Select(call => (JsonNode)new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToString() }
                }).ToArray())
            });
            foreach (var call in calls.Values)
            {
                await emit("tool_call", new { id = call.Id, name = call.Name, arguments = call.Arguments.ToString() });
                var result = await _tools.ExecuteAsync(call.Name, call.Arguments.ToString());
                await emit("tool_result", new { id = call.Id, name = call.Name, result });
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result });
            }
        }

        await emit("content", new { text = "\n\nI stopped after too many tool calls. Please ask a narrower question." });
        await emit("done", new { });
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class ToolCall
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }
}
