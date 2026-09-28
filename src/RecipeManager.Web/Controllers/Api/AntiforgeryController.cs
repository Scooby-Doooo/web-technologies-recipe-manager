using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace RecipeManager.Web.Controllers.Api;

[ApiController]
[Route("api/antiforgery")]
public class AntiforgeryController : ControllerBase
{
    private readonly IAntiforgery _antiforgery;

    public AntiforgeryController(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    // API clients retain the cookie and send requestToken in the returned headerName.
    [HttpGet]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Get()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { tokens.RequestToken, tokens.HeaderName });
    }
}
