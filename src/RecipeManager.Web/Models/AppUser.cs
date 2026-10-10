using System.ComponentModel.DataAnnotations;

namespace RecipeManager.Web.Models;

// A local account, created the first time someone signs in with an external provider.
public class AppUser
{
    public int Id { get; set; }

    // Provider name plus its stable user id, e.g. "Google:1084...". Email can change, this cannot.
    [Required]
    [StringLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    [Required]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    public DateTime RegisteredAt { get; set; }

    public DateTime LastSignInAt { get; set; }

    // The kitchen the user works in. Null only for accounts created before kitchens existed;
    // they get a kitchen on their next sign-in.
    public int? TenantId { get; set; }

    public Tenant? Tenant { get; set; }
}
