namespace RecipeManager.Web.Models;

public class AccountViewModel
{
    public required AppUser User { get; init; }
    public required Tenant Kitchen { get; init; }
    public required IReadOnlyList<AppUser> Members { get; init; }
}
