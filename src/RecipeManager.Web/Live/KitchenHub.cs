using Microsoft.AspNetCore.SignalR;
using RecipeManager.Web.Data;

namespace RecipeManager.Web.Live;

// SignalR version of the live feed (C11). Each connection joins the group of its kitchen, and
// KitchenFeedInterceptor sends every change to that group as a "changes" message.
public class KitchenHub : Hub
{
    private readonly KitchenFeed _feed;

    public KitchenHub(KitchenFeed feed)
    {
        _feed = feed;
    }

    public static string Group(int tenantId) => "kitchen-" + tenantId;

    // The kitchen comes from the connection's own user; hubs should not read IHttpContextAccessor.
    private int TenantId => TenantContext.FromUser(Context.User);

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(TenantId));
        await base.OnConnectedAsync();
    }

    // After connecting or reconnecting the client asks for what it missed, in the same shape as the C10 answers.
    public object Since(long since)
    {
        var (last, events) = _feed.Since(TenantId, since);
        return new { last, events };
    }
}
