using System.ComponentModel.DataAnnotations;

namespace RecipeManager.Web.Models;

// A one-time code from the account page. Opening t.me/<bot>?start=<code> links that chat to the kitchen.
public class TelegramLinkCode
{
    [Key]
    [StringLength(16)]
    public string Code { get; set; } = string.Empty;

    public int TenantId { get; set; }

    public DateTime ExpiresAt { get; set; }
}
