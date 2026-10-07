using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Antiforgery cookies are encrypted with these keys; in a container they must outlive the container.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrEmpty(keysPath))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Browsers use the cookie from the Google sign-in; API clients such as the MCP server
// send a bearer token issued on the account page. The policy scheme picks one per request.
const string CookieOrBearer = "CookieOrBearer";
var authentication = builder.Services.AddAuthentication(CookieOrBearer)
    .AddPolicyScheme(CookieOrBearer, null, options => options.ForwardDefaultSelector = context =>
        context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? BearerTokenDefaults.AuthenticationScheme
            : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        // The API answers with a status code; only MVC pages redirect to the sign-in page.
        options.Events.OnRedirectToLogin = context => RedirectOrStatus(context, StatusCodes.Status401Unauthorized);
        options.Events.OnRedirectToAccessDenied = context => RedirectOrStatus(context, StatusCodes.Status403Forbidden);
    })
    .AddBearerToken(options => options.BearerTokenExpiration = TimeSpan.FromHours(8));

var google = builder.Configuration.GetSection("Authentication:Google");
if (!string.IsNullOrEmpty(google["ClientId"]))
{
    authentication.AddGoogle(options =>
    {
        options.ClientId = google["ClientId"]!;
        options.ClientSecret = google["ClientSecret"]!;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Events.OnCreatingTicket = async context => await UserRegistration.RegisterOrUpdateAsync(
            context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>(), context.Identity!, "Google");
    });
}

builder.Services.AddScoped<OutputCacheInvalidationInterceptor>();
builder.Services.AddDbContext<ApplicationDbContext>((services, options) => options
    .UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"))
    .AddInterceptors(services.GetRequiredService<OutputCacheInvalidationInterceptor>()));

// GET responses of the JSON API are kept in memory; saves evict them by tag.
var apiCacheDuration = TimeSpan.FromSeconds(builder.Configuration.GetValue("ApiCache:DurationSeconds", 60));
builder.Services.AddOutputCache(options =>
{
    foreach (var tag in new[] { OutputCacheInvalidationInterceptor.RecipesTag,
                 OutputCacheInvalidationInterceptor.CategoriesTag, OutputCacheInvalidationInterceptor.IngredientsTag })
    {
        // "*" keeps "?skip=" (invalid, 400) apart from a request without skip (plain array).
        options.AddPolicy(tag, policy => policy.AddPolicy<PublicApiCachePolicy>()
            .Expire(apiCacheDuration).SetVaryByQuery("*").Tag(tag), excludeDefaultPolicy: true);
    }
});

var app = builder.Build();

// A container starts with an empty volume, so it can create or upgrade the database itself.
if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// API failures return JSON, while MVC keeps its existing error page.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api =>
{
    api.UseExceptionHandler(error =>
    {
        error.Run(context => Results.Problem(title: "Unable to process the API request.",
            statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context));
    });
});

app.UseHttpsRedirection();
// Uploaded images are created at runtime and are not in the build-time static asset manifest.
app.UseStaticFiles();
app.UseRouting();
app.UseOutputCache();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllers();

// Lets automated tests sign in without Google. It exists only in Development and only when
// Authentication:EnableTestSignIn is set, which the E2E and Postman test runs do.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Authentication:EnableTestSignIn"))
{
    app.MapPost("/Account/TestSignIn", async (HttpContext http, ApplicationDbContext db, string? email) =>
    {
        email = string.IsNullOrWhiteSpace(email) ? "qa@example.test" : email.Trim();
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, email),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, "Test user " + email)
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await UserRegistration.RegisterOrUpdateAsync(db, identity, "Test");
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.NoContent();
    });
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
{
    if (context.Request.Path.StartsWithSegments("/api"))
        context.Response.StatusCode = statusCode;
    else
        context.Response.Redirect(context.RedirectUri);
    return Task.CompletedTask;
}
