using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RecipeManager.Web.Data;
using RecipeManager.Web.Models;

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
        var model = await AccountModel();
        if (model == null) return await SignOutMissingUser();
        return View(model);
    }

    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameKitchen(string? name)
    {
        var user = await CurrentUser(tracking: true);
        if (user?.Tenant == null) return await SignOutMissingUser();
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
        {
            TempData["AccountError"] = "Kitchen name must have from 1 to 100 characters.";
            return RedirectToAction(nameof(Index));
        }
        user.Tenant.Name = name;
        await _context.SaveChangesAsync();
        await RefreshKitchenClaims(user.Tenant);
        TempData["AccountMessage"] = "Kitchen renamed.";
        return RedirectToAction(nameof(Index));
    }

    // Moves the user into another kitchen. Their previous kitchen and its recipes stay as they are.
    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> JoinKitchen(string? inviteCode)
    {
        var user = await CurrentUser(tracking: true);
        if (user == null) return await SignOutMissingUser();
        var code = inviteCode?.Trim().ToUpperInvariant();
        var kitchen = string.IsNullOrEmpty(code) ? null : await _context.Tenants.FirstOrDefaultAsync(tenant => tenant.InviteCode == code);
        if (kitchen == null)
        {
            TempData["AccountError"] = "No kitchen has this invite code.";
            return RedirectToAction(nameof(Index));
        }
        user.Tenant = kitchen;
        await _context.SaveChangesAsync();
        await RefreshKitchenClaims(kitchen);
        TempData["AccountMessage"] = $"You joined {kitchen.Name}.";
        return RedirectToAction(nameof(Index));
    }

    // Issues a bearer token for API clients that cannot use the browser cookie, e.g. the MCP server.
    [Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ApiToken()
    {
        var model = await AccountModel();
        if (model == null) return await SignOutMissingUser();

        var options = _bearerOptions.Get(BearerTokenDefaults.AuthenticationScheme);
        var expires = DateTimeOffset.UtcNow.Add(options.BearerTokenExpiration);
        var identity = new ClaimsIdentity(User.Claims, BearerTokenDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = expires }, BearerTokenDefaults.AuthenticationScheme);

        ViewData["ApiToken"] = options.BearerTokenProtector.Protect(ticket);
        ViewData["ApiTokenExpires"] = expires;
        return View(nameof(Index), model);
    }

    private async Task<AppUser?> CurrentUser(bool tracking = false)
    {
        if (!int.TryParse(User.FindFirst(UserRegistration.UserIdClaim)?.Value, out var id)) return null;
        var users = tracking ? _context.Users : _context.Users.AsNoTracking();
        return await users.Include(user => user.Tenant).FirstOrDefaultAsync(user => user.Id == id);
    }

    private async Task<AccountViewModel?> AccountModel()
    {
        var user = await CurrentUser();
        if (user?.Tenant == null) return null;
        var members = await _context.Users.AsNoTracking()
            .Where(member => member.TenantId == user.TenantId)
            .OrderBy(member => member.RegisteredAt)
            .ToListAsync();
        return new AccountViewModel { User = user, Kitchen = user.Tenant, Members = members };
    }

    // The kitchen lives in the cookie's claims, so the cookie is reissued after it changes.
    // Bearer tokens issued earlier keep their old kitchen until they expire.
    private async Task RefreshKitchenClaims(Tenant kitchen)
    {
        var identity = new ClaimsIdentity(User.Claims, CookieAuthenticationDefaults.AuthenticationScheme);
        UserRegistration.AddKitchenClaims(identity, kitchen);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    // A cookie can outlive its account, e.g. after the database is recreated.
    private async Task<IActionResult> SignOutMissingUser()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
}
