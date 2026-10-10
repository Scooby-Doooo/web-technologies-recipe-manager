namespace RecipeManager.Web.Models;

// Marks entities that are isolated per kitchen; ApplicationDbContext filters and stamps them.
public interface ITenantOwned
{
    int TenantId { get; set; }
}
