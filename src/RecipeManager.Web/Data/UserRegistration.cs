using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Data;

// Turns an external sign-in into a local account: the first sign-in registers the user,
// later ones refresh the name and email that the provider reports.
public static class UserRegistration
{
    public const string UserIdClaim = "recipe_manager:user_id";

    public static async Task<AppUser> RegisterOrUpdateAsync(ApplicationDbContext db, ClaimsIdentity identity, string provider)
    {
        var providerUserId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException($"{provider} did not return a user id.");
        var externalId = $"{provider}:{providerUserId}";
        var email = identity.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
        var name = identity.FindFirst(ClaimTypes.Name)?.Value;
        var now = DateTime.UtcNow;

        var user = await db.Users.FirstOrDefaultAsync(item => item.ExternalId == externalId);
        if (user == null)
        {
            user = new AppUser { ExternalId = externalId, RegisteredAt = now };
            db.Users.Add(user);
        }
        user.Email = email;
        user.DisplayName = string.IsNullOrWhiteSpace(name) ? email : name;
        user.LastSignInAt = now;
        await db.SaveChangesAsync();

        identity.AddClaim(new Claim(UserIdClaim, user.Id.ToString()));
        return user;
    }
}
