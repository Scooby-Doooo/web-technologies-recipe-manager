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

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllers();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
