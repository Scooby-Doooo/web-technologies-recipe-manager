using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RecipeManager.Web.Data;

namespace RecipeManager.Web.Controllers;

public class AccountController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAuthenticationSchemeProvider _schemes;
    private readonly IOptionsMonitor<BearerTokenOptions> _bearerOptions;

    public AccountController(ApplicationDbContext context, IAuthenticationSchemeProvider schemes,
        IOptionsMonitor<BearerTokenOptions> bearerOptions)
    {
        _context = context;
        _schemes = schemes;
        _bearerOptions = bearerOptions;
    }

    public async Task<IActionResult> Login(string? returnUrl)
    {
        returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action("Index", "Home");
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(returnUrl!);

        // Without a configured client id the Google handler is not registered at all.
        ViewData["GoogleConfigured"] = await _schemes.GetSchemeAsync("Google") != null;
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExternalLogin(string? returnUrl)
    {
        if (await _schemes.GetSchemeAsync("Google") == null) return RedirectToAction(nameof(Login));
        returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Action("Index", "Home");
        return Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, "Google");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Home");
    }

    [Authorize]
    public async Task<IActionResult> Index()
    {
        var user = await CurrentUser();
        if (user == null) return await SignOutMissingUser();
        return View(user);
    }

    // Issues a bearer token for API clients that cannot use the browser cookie, e.g. the MCP server.
    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ApiToken()
    {
        var user = await CurrentUser();
        if (user == null) return await SignOutMissingUser();

        var options = _bearerOptions.Get(BearerTokenDefaults.AuthenticationScheme);
        var expires = DateTimeOffset.UtcNow.Add(options.BearerTokenExpiration);
        var identity = new ClaimsIdentity(User.Claims, BearerTokenDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = expires }, BearerTokenDefaults.AuthenticationScheme);

        ViewData["ApiToken"] = options.BearerTokenProtector.Protect(ticket);
        ViewData["ApiTokenExpires"] = expires;
        return View(nameof(Index), user);
    }

    private Task<Models.AppUser?> CurrentUser()
    {
        return int.TryParse(User.FindFirst(UserRegistration.UserIdClaim)?.Value, out var id)
            ? _context.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == id)
            : Task.FromResult<Models.AppUser?>(null);
    }

    // A cookie can outlive its account, e.g. after the database is recreated.
    private async Task<IActionResult> SignOutMissingUser()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
}
