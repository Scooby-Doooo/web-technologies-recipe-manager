namespace RecipeManager.Web.Live;

// Browsers send the sign-in cookie with a WebSocket handshake from any site, and the same-origin
// policy does not apply to WebSockets, so another site could read a kitchen's feed
// (cross-site WebSocket hijacking). Clients outside a browser send no Origin and pass.
public static class OriginCheck
{
    public static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0
               || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }
}
