using System.ComponentModel.DataAnnotations;

namespace RecipeManager.Web.Models;

// A Telegram chat linked to a kitchen: it can browse the kitchen's recipes through the bot
// and gets a message when a recipe is added.
public class TelegramChat : ITenantOwned
{
    public int Id { get; set; }

    public long ChatId { get; set; }

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    public int TenantId { get; set; }

    public DateTime LinkedAt { get; set; }
}
