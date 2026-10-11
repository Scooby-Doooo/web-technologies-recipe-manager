using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;
using RecipeManager.Web.Data;

namespace RecipeManager.Web.Features;

// Enables a feature when the current kitchen's plan is listed in the flag's "Plans" parameter.
// The plan is read from the database, so a plan change applies on the next request.
[FilterAlias("Plan")]
public class PlanFeatureFilter : IFeatureFilter
{
    private readonly IHttpContextAccessor _http;

    public PlanFeatureFilter(IHttpContextAccessor http)
    {
        _http = http;
    }

    public async Task<bool> EvaluateAsync(FeatureFilterEvaluationContext context)
    {
        var services = _http.HttpContext?.RequestServices;
        if (services == null) return false;
        var plans = context.Parameters.GetSection("Plans").Get<string[]>() ?? [];
        var tenantId = services.GetRequiredService<TenantContext>().TenantId;
        var plan = await services.GetRequiredService<ApplicationDbContext>().Tenants.AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Plan)
            .FirstOrDefaultAsync();
        return plan != null && plans.Contains(plan, StringComparer.OrdinalIgnoreCase);
    }
}
