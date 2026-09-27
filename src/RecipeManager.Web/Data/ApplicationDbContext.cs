using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Models;

namespace RecipeManager.Web.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Recipe> Recipes { get; set; }
    public DbSet<Category> Categories { get; set; }
    public DbSet<Ingredient> Ingredients { get; set; }
    public DbSet<RecipeIngredient> RecipeIngredients { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RecipeIngredient>()
            .HasKey(item => new { item.RecipeId, item.IngredientId });

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

        modelBuilder.Entity<Category>().HasData(
            new Category { Id = 1, Name = "Breakfast" },
            new Category { Id = 2, Name = "Main Course" },
            new Category { Id = 3, Name = "Dessert" },
            new Category { Id = 4, Name = "Salad" });

        modelBuilder.Entity<Ingredient>().HasData(
            new Ingredient { Id = 1, Name = "Egg" },
            new Ingredient { Id = 2, Name = "Flour" },
            new Ingredient { Id = 3, Name = "Milk" },
            new Ingredient { Id = 4, Name = "Tomato" },
            new Ingredient { Id = 5, Name = "Chicken Breast" },
            new Ingredient { Id = 6, Name = "Parmesan" },
            new Ingredient { Id = 7, Name = "Spaghetti" });
    }
}
