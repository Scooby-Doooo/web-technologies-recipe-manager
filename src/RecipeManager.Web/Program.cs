using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.FeatureManagement;
using Npgsql;
using RecipeManager.Web.Assistant;
using RecipeManager.Web.Data;
using RecipeManager.Web.Features;
using RecipeManager.Web.Live;
using RecipeManager.Web.MealDb;
using RecipeManager.Web.Messaging;
using RecipeManager.Web.Photos;
using RecipeManager.Web.Search;
using RecipeManager.Web.Telegram;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Sign-in and antiforgery cookies are encrypted with these keys. They live in the database, so
// they outlive a container whose disk is wiped on every deploy (Render).
builder.Services.AddDataProtection().PersistKeysToDbContext<ApplicationDbContext>();

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
        // Cookies issued before kitchens existed have no kitchen; such users simply sign in again.
        options.Events.OnValidatePrincipal = async context =>
        {
            if (context.Principal?.FindFirst(TenantContext.TenantIdClaim) == null)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    })
    .AddBearerToken(options => options.BearerTokenExpiration =
        builder.Configuration.GetValue("Authentication:ApiTokenLifetime", TimeSpan.FromHours(8)));

// [Authorize] also needs a kitchen, so an old token without one cannot write into the public kitchen.
builder.Services.AddAuthorization(options => options.DefaultPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .RequireClaim(TenantContext.TenantIdClaim)
    .Build());
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TenantContext>();

// Plans are feature flags: the "Plan" filter turns a flag on for kitchens on the listed plans.
builder.Services.AddFeatureManagement()
    .AddFeatureFilter<PlanFeatureFilter>()
    .UseDisabledFeaturesHandler((_, context) => context.Result = new ViewResult
    {
        ViewName = "PremiumRequired",
        StatusCode = StatusCodes.Status403Forbidden,
        ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), context.ModelState)
    });
builder.Services.AddScoped<RecipeLimit>();

// Emails that join the public demo kitchen on sign-in, so its owner can edit the public recipes.
// Kept in user secrets or environment variables, not in the repository.
var publicKitchenMembers = builder.Configuration.GetSection("Tenancy:PublicKitchenMembers").Get<string[]>() ?? [];

var google = builder.Configuration.GetSection("Authentication:Google");
if (!string.IsNullOrEmpty(google["ClientId"]))
{
    authentication.AddGoogle(options =>
    {
        options.ClientId = google["ClientId"]!;
        options.ClientSecret = google["ClientSecret"]!;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Events.OnCreatingTicket = async context => await UserRegistration.RegisterOrUpdateAsync(
            context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>(), context.Identity!, "Google",
            joinPublicKitchen: publicKitchenMembers.Contains(context.Identity!.FindFirst(ClaimTypes.Email)?.Value,
                StringComparer.OrdinalIgnoreCase));
    });
}

// AI assistant (C21). The OpenRouter key comes from user secrets or environment variables.
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection("OpenRouter"));
builder.Services.AddHttpClient<OpenRouterClient>();
builder.Services.AddScoped<AssistantTools>();
builder.Services.AddScoped<AssistantAgent>();

// Telegram bot (B2). Without Telegram:BotToken nothing below starts and notifications are skipped.
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection("Telegram"));
var telegram = builder.Configuration.GetSection("Telegram").Get<TelegramOptions>() ?? new TelegramOptions();
builder.Services.AddSingleton(Channel.CreateBounded<RecipeCreated>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest }));
builder.Services.AddScoped<RecipeCreatedNotifier>();
builder.Services.AddSingleton<TelegramClient>();
builder.Services.AddScoped<TelegramBot>();
builder.Services.AddSingleton<TelegramService>();
if (telegram.Enabled)
    builder.Services.AddHostedService(services => services.GetRequiredService<TelegramService>());

// New-recipe announcements go through RabbitMQ when RabbitMq:Url is set (B8): durable, retried
// after a delay and parked in a failed queue at the end. Otherwise an in-memory queue is used.
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
if (builder.Configuration.GetSection("RabbitMq").Get<RabbitMqOptions>()?.Enabled == true)
{
    builder.Services.AddSingleton<RabbitMqBus>();
    builder.Services.AddSingleton<IRecipeCreatedPublisher, RabbitMqRecipeCreatedPublisher>();
    if (telegram.Enabled) builder.Services.AddHostedService<TelegramQueueConsumer>();
}
else
    builder.Services.AddSingleton<IRecipeCreatedPublisher, ChannelRecipeCreatedPublisher>();

// Recipe photos (C19): Supabase Storage when its URL and secret key are set, else wwwroot/uploads.
builder.Services.Configure<SupabaseStorageOptions>(builder.Configuration.GetSection("Supabase"));
if (builder.Configuration.GetSection("Supabase").Get<SupabaseStorageOptions>()?.Enabled == true)
    builder.Services.AddHttpClient<IPhotoStorage, SupabasePhotoStorage>();
else
    builder.Services.AddSingleton<IPhotoStorage, LocalPhotoStorage>();

// Recipe import from TheMealDB (C8). Its free test key needs no secret.
builder.Services.AddMemoryCache();
builder.Services.Configure<TheMealDbOptions>(builder.Configuration.GetSection("TheMealDb"));
builder.Services.AddHttpClient<TheMealDbClient>();
builder.Services.AddScoped<MealImporter>();

// Live feed of recipe changes (C10).
builder.Services.AddSingleton<KitchenFeed>();
// Notes changes carry whole blocks (NotesChange.MaxText characters each), more than the default 32 KB.
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 512 * 1024);
builder.Services.AddScoped<KitchenFeedInterceptor>();

// Full-text recipe search with Lucene.NET (B4): an in-memory index rebuilt per kitchen after changes.
builder.Services.AddSingleton<RecipeSearchIndex>();
builder.Services.AddScoped<RecipeSearchInterceptor>();

builder.Services.AddScoped<OutputCacheInvalidationInterceptor>();
builder.Services.AddDbContext<ApplicationDbContext>((services, options) => options
    // GSSAPI (Kerberos) is not used, and its native library is not in the ASP.NET image; without this
    // Npgsql tries it on every new connection and logs an error.
    .UseNpgsql(new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString("DefaultConnection"))
        { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString)
    .AddInterceptors(services.GetRequiredService<OutputCacheInvalidationInterceptor>(),
        services.GetRequiredService<RecipeCreatedNotifier>(), services.GetRequiredService<KitchenFeedInterceptor>(),
        services.GetRequiredService<RecipeSearchInterceptor>()));

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
// The live feed's WebSocket; pings keep idle connections open through proxies.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseRouting();
// Authentication runs first, so the output cache can keep each kitchen's API responses apart.
app.UseAuthentication();
app.UseOutputCache();
app.UseAuthorization();

app.MapStaticAssets();

app.MapControllers();
// SignalR version of the live feed (C11). Its WebSocket transport needs the same Origin check as C10.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/hubs"), hub => hub.Use(async (context, next) =>
{
    if (OriginCheck.IsSameOrigin(context.Request)) await next(context);
    else context.Response.StatusCode = StatusCodes.Status403Forbidden;
}));
app.MapHub<KitchenHub>("/hubs/kitchen");

// Telegram calls this when Telegram:WebhookUrl is set (a public https address is needed).
// The secret header proves the call comes from Telegram, since the URL itself is public.
if (telegram.Enabled && !string.IsNullOrWhiteSpace(telegram.WebhookUrl))
{
    app.MapPost("/telegram/webhook", async (HttpContext http, TelegramService bot) =>
    {
        var secret = http.Request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
        if (string.IsNullOrEmpty(telegram.WebhookSecret)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(telegram.WebhookSecret)))
            return Results.Unauthorized();
        var update = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: http.RequestAborted);
        await bot.HandleUpdateAsync(update, http.RequestAborted);
        return Results.Ok();
    });
}

// Lets automated tests sign in without Google. It exists only in Development and only when
// Authentication:EnableTestSignIn is set, which the E2E and Postman test runs do.
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Authentication:EnableTestSignIn"))
{
    // Test users join the public kitchen with its seeded data unless a test asks for an own kitchen.
    app.MapPost("/Account/TestSignIn", async (HttpContext http, ApplicationDbContext db, string? email, string? kitchen) =>
    {
        email = string.IsNullOrWhiteSpace(email) ? "qa@example.test" : email.Trim();
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, email),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, "Test user " + email)
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await UserRegistration.RegisterOrUpdateAsync(db, identity, "Test", joinPublicKitchen: kitchen != "own");
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
