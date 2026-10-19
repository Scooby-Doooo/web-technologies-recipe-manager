using System.Security.Claims;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Data;

// The kitchen of the current request. Signed-in users carry it in a claim of their cookie or
// bearer token; anonymous visitors (and design-time tools) see the public kitchen.
// Scoped, so an explicit kitchen set by background work stays inside that scope.
public class TenantContext
{
    public const string TenantIdClaim = "recipe_manager:tenant_id";
    public const string TenantNameClaim = "recipe_manager:tenant_name";

    private readonly IHttpContextAccessor _http;

    public TenantContext(IHttpContextAccessor http)
    {
        _http = http;
    }

    private int? _explicitTenantId;

    public int TenantId => _explicitTenantId ?? FromUser(_http.HttpContext?.User);

    public static int FromUser(ClaimsPrincipal? user) =>
        int.TryParse(user?.FindFirst(TenantIdClaim)?.Value, out var id) ? id : Tenant.PublicId;

    // Background work has no signed-in user, e.g. the Telegram bot answering a linked chat.
    // It sets the kitchen explicitly for its own scope.
    public void UseTenant(int tenantId) => _explicitTenantId = tenantId;
}
