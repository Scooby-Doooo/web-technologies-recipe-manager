using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Data;

// Turns an external sign-in into a local account: the first sign-in registers the user,
// later ones refresh the name and email that the provider reports.
public static class UserRegistration
{
    public const string UserIdClaim = "recipe_manager:user_id";

    public static async Task<AppUser> RegisterOrUpdateAsync(ApplicationDbContext db, ClaimsIdentity identity,
        string provider, bool joinPublicKitchen = false)
    {
        var providerUserId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException($"{provider} did not return a user id.");
        var externalId = $"{provider}:{providerUserId}";
        var email = identity.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
        var name = identity.FindFirst(ClaimTypes.Name)?.Value;
        var now = DateTime.UtcNow;

        var user = await db.Users.Include(item => item.Tenant).FirstOrDefaultAsync(item => item.ExternalId == externalId);
        if (user == null)
        {
            user = new AppUser { ExternalId = externalId, RegisteredAt = now };
            db.Users.Add(user);
        }
        user.Email = email;
        user.DisplayName = string.IsNullOrWhiteSpace(name) ? email : name;
        user.LastSignInAt = now;

        if (joinPublicKitchen && user.TenantId != Tenant.PublicId)
            user.Tenant = await db.Tenants.SingleAsync(tenant => tenant.Id == Tenant.PublicId);
        else if (user.Tenant == null)
            user.Tenant = await CreateKitchenAsync(db, $"{user.DisplayName}'s kitchen");
        await db.SaveChangesAsync();

        identity.AddClaim(new Claim(UserIdClaim, user.Id.ToString()));
        AddKitchenClaims(identity, user.Tenant!);
        return user;
    }

    // A new kitchen gets its own copy of the default categories and ingredients.
    public static async Task<Tenant> CreateKitchenAsync(ApplicationDbContext db, string name)
    {
        var tenant = new Tenant { Name = name.Length > 100 ? name[..100] : name, InviteCode = NewInviteCode(), CreatedAt = DateTime.UtcNow };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(); // The copies below need the new kitchen's id.
        db.Categories.AddRange(DefaultKitchenData.Categories.Select(item => new Category { Name = item, TenantId = tenant.Id }));
        db.Ingredients.AddRange(DefaultKitchenData.Ingredients.Select(item => new Ingredient { Name = item, TenantId = tenant.Id }));
        return tenant;
    }

    public static void AddKitchenClaims(ClaimsIdentity identity, Tenant tenant)
    {
        foreach (var claim in identity.FindAll(item => item.Type is TenantContext.TenantIdClaim or TenantContext.TenantNameClaim).ToList())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(TenantContext.TenantIdClaim, tenant.Id.ToString()));
        identity.AddClaim(new Claim(TenantContext.TenantNameClaim, tenant.Name));
    }

    // 10 characters from an alphabet without look-alikes (0/O, 1/I/L): about 50 bits, not guessable.
    public static string NewInviteCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return RandomNumberGenerator.GetString(alphabet, 10);
    }
}
