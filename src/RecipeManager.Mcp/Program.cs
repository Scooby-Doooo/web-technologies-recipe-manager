using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RecipeManager.Mcp;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries MCP messages, so every log line must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var apiUrl = builder.Configuration["RECIPE_API_URL"] ?? "http://localhost:5105";
var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
{
    BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromSeconds(15)
};
// Reading works anonymously; the write tools need a token from the web app's account page.
var apiToken = builder.Configuration["RECIPE_API_TOKEN"];
if (!string.IsNullOrWhiteSpace(apiToken))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
builder.Services.AddSingleton(new RecipeApiClient(http));

builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "recipe-manager", Version = "1.0.0" })
    .WithStdioServerTransport()
    .WithTools<RecipeTools>();

await builder.Build().RunAsync();
