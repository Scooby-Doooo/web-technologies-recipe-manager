using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeManager.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddMealDbImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Recipes_TenantId",
                table: "Recipes");

            migrationBuilder.AddColumn<int>(
                name: "MealDbId",
                table: "Recipes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_TenantId_MealDbId",
                table: "Recipes",
                columns: new[] { "TenantId", "MealDbId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Recipes_TenantId_MealDbId",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "MealDbId",
                table: "Recipes");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_TenantId",
                table: "Recipes",
                column: "TenantId");
        }
    }
}
