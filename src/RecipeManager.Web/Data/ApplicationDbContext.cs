using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Data;

public class ApplicationDbContext : DbContext
{
    private readonly TenantContext _tenant;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, TenantContext tenant)
        : base(options)
    {
        _tenant = tenant;
    }

    // Read by the query filters on every query, so each request sees only its own kitchen.
    public int CurrentTenantId => _tenant.TenantId;

    public DbSet<Recipe> Recipes { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<Ingredient> Ingredients { get; set; }
    public DbSet<RecipeIngredient> RecipeIngredients { get; set; }
    public DbSet<AppUser> Users { get; set; }
    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<TelegramChat> TelegramChats { get; set; }
    public DbSet<TelegramLinkCode> TelegramLinkCodes { get; set; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // New rows join the current kitchen unless code sets a kitchen on purpose (seeding a new one).
    // Changing another kitchen's rows is a bug, not a user error.
    private void StampTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == 0)
                entry.Entity.TenantId = CurrentTenantId;
            else if ((entry.State is EntityState.Modified or EntityState.Deleted) && entry.Entity.TenantId != CurrentTenantId)
                throw new InvalidOperationException("A change crosses kitchens.");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RecipeIngredient>()
            .HasKey(item => new { item.RecipeId, item.IngredientId });

        modelBuilder.Entity<AppUser>()
            .HasIndex(user => user.ExternalId)
            .IsUnique();

        modelBuilder.Entity<Tenant>()
            .HasIndex(tenant => tenant.InviteCode)
            .IsUnique();

        modelBuilder.Entity<AppUser>()
            .HasOne(user => user.Tenant)
            .WithMany()
            .HasForeignKey(user => user.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Recipe>().HasOne<Tenant>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Category>().HasOne<Tenant>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Ingredient>().HasOne<Tenant>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Recipe>().HasQueryFilter(item => item.TenantId == CurrentTenantId);
        modelBuilder.Entity<Category>().HasQueryFilter(item => item.TenantId == CurrentTenantId);
        modelBuilder.Entity<Ingredient>().HasQueryFilter(item => item.TenantId == CurrentTenantId);
        modelBuilder.Entity<RecipeIngredient>().HasQueryFilter(item => item.Recipe.TenantId == CurrentTenantId);
        modelBuilder.Entity<TelegramChat>().HasQueryFilter(item => item.TenantId == CurrentTenantId);

        modelBuilder.Entity<TelegramChat>().HasIndex(item => item.ChatId).IsUnique();
        // SQLite allows many NULLs in a unique index, so recipes typed by hand are not affected.
        modelBuilder.Entity<Recipe>().HasIndex(item => new { item.TenantId, item.MealDbId }).IsUnique();
        modelBuilder.Entity<TelegramChat>().HasOne<Tenant>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TelegramLinkCode>().HasOne<Tenant>().WithMany().HasForeignKey(item => item.TenantId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Tenant>().HasData(new Tenant
        {
            Id = Tenant.PublicId,
            Name = "Public demo kitchen",
            // The showcase kitchen has every feature, so its recipes are never capped.
            Plan = Features.Plans.Premium,
            CreatedAt = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc)
        });

        modelBuilder.Entity<Recipe>()
            .HasOne(recipe => recipe.Category)
            .WithMany(category => category.Recipes)
            .HasForeignKey(recipe => recipe.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RecipeIngredient>()
            .HasOne(item => item.Recipe)
            .WithMany(recipe => recipe.RecipeIngredients)
            .HasForeignKey(item => item.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RecipeIngredient>()
            .HasOne(item => item.Ingredient)
            .WithMany(ingredient => ingredient.RecipeIngredients)
            .HasForeignKey(item => item.IngredientId)
            .OnDelete(DeleteBehavior.Restrict);

        // The public kitchen starts with the same reference data that every new kitchen gets.
        modelBuilder.Entity<Category>().HasData(DefaultKitchenData.Categories
            .Select((name, index) => new Category { Id = index + 1, Name = name, TenantId = Tenant.PublicId }));
        modelBuilder.Entity<Ingredient>().HasData(DefaultKitchenData.Ingredients
            .Select((name, index) => new Ingredient { Id = index + 1, Name = name, TenantId = Tenant.PublicId }));
    }
}
