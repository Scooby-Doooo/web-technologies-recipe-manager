using System.Security.Claims;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Data;

// Kitchen of the current request: from the user's claim, or the public kitchen for anonymous visitors.
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

    // Background work (the Telegram bot) has no user, so it sets the kitchen itself.
    public void UseTenant(int tenantId) => _explicitTenantId = tenantId;
}
