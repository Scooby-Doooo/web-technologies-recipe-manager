using RecipeManager.Web.Models;

namespace RecipeManager.Web.Data;

// The kitchen of the current request. Signed-in users carry it in a claim of their cookie or
// bearer token; anonymous visitors (and design-time tools) see the public kitchen.
public class TenantContext
{
    public const string TenantIdClaim = "recipe_manager:tenant_id";
    public const string TenantNameClaim = "recipe_manager:tenant_name";

    private readonly IHttpContextAccessor _http;

    public TenantContext(IHttpContextAccessor http)
    {
        _http = http;
    }

    public int TenantId =>
        int.TryParse(_http.HttpContext?.User.FindFirst(TenantIdClaim)?.Value, out var id) ? id : Tenant.PublicId;
}
