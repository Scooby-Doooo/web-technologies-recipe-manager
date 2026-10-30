using Microsoft.AspNetCore.Mvc;
using RecipeManager.Web.Data;

namespace RecipeManager.Web.Controllers.Api;

// Tells a separate front end (the F5 SPA) who is signed in. Anonymous visitors get signedIn: false
// instead of 401, because for them the answer is normal, not an error.
[ApiController]
[Route("api/account")]
public class AccountApiController : ControllerBase
{
    [HttpGet]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Get()
    {
        var kitchen = User.FindFirst(TenantContext.TenantNameClaim)?.Value;
        return User.Identity?.IsAuthenticated == true && kitchen != null
            ? Ok(new { SignedIn = true, User.Identity.Name, Kitchen = kitchen })
            : Ok(new { SignedIn = false, Name = (string?)null, Kitchen = (string?)null });
    }
}
