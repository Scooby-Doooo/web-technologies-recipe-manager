using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using RecipeManager.Web.Data;
using RecipeManager.Web.Live;

namespace RecipeManager.Web.Controllers;

// Live feed of recipe changes in the current kitchen: plain requests for (adaptive) polling,
// long polling and a WebSocket.
public class LiveController : Controller
{
    private static readonly JsonSerializerOptions SocketJson = new(JsonSerializerDefaults.Web);
    private readonly KitchenFeed _feed;
    private readonly TenantContext _tenant;
    private readonly TimeSpan _longPollTimeout;
    private readonly CancellationToken _stopping;

    public LiveController(KitchenFeed feed, TenantContext tenant, IConfiguration configuration, IHostApplicationLifetime lifetime)
    {
        _feed = feed;
        _tenant = tenant;
        // Open waits end on shutdown instead of holding it back.
        _stopping = lifetime.ApplicationStopping;
        _longPollTimeout = TimeSpan.FromSeconds(configuration.GetValue("Live:LongPollSeconds", 25));
    }

    public IActionResult Index() => View();

    // Answers at once, with or without new events.
    [HttpGet]
    public IActionResult Events(long since = 0) => Answer(since);

    // Holds the request until the kitchen changes or the timeout passes, then answers like Events.
    [HttpGet]
    public async Task<IActionResult> Wait(long since = 0)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, _stopping);
        await _feed.WaitAsync(_tenant.TenantId, since, _longPollTimeout, stop.Token);
        return Answer(since);
    }

    // One message per batch of events, in the same JSON shape as the HTTP answers.
    [HttpGet]
    public async Task Socket(long since = 0)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!OriginCheck.IsSameOrigin(Request))
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var tenantId = _tenant.TenantId;
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, _stopping);
        var receiving = ReceiveUntilClosedAsync(socket, stop);
        try
        {
            var first = true;
            while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var (last, events) = _feed.Since(tenantId, since);
                if (first || events.Count > 0 || last != since)
                {
                    var message = JsonSerializer.SerializeToUtf8Bytes(new { last, events }, SocketJson);
                    await socket.SendAsync(message, WebSocketMessageType.Text, true, stop.Token);
                    since = last;
                    first = false;
                }
                await _feed.WaitAsync(tenantId, since, Timeout.InfiniteTimeSpan, stop.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
            // The client left; nothing to send any more.
        }
        await stop.CancelAsync();
        await receiving;
        // Only this loop sends, so the close frame is answered here and never overlaps a SendAsync.
        if (socket.State is WebSocketState.CloseReceived or WebSocketState.Open)
        {
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch (WebSocketException) { }
        }
    }

    private IActionResult Answer(long since)
    {
        var (last, events) = _feed.Since(_tenant.TenantId, since);
        Response.Headers.CacheControl = "no-store";
        return Json(new { last, events });
    }

    // The client sends nothing, but reading is how a close frame or a dropped connection is noticed.
    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationTokenSource stop)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }
        await stop.CancelAsync();
    }
}
