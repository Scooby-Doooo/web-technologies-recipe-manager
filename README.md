# Recipe Manager

Semester project for the Web Technologies course.

## Description

Recipe Manager is a simple ASP.NET Core MVC web application for managing recipes,
recipe categories and ingredients. The project is developed gradually as a single MVC application.

Recipes, categories and ingredients each support list, details, create, edit and
delete pages. Recipe forms include a category selector, description, instructions,
cooking time and servings. DataAnnotations provide server validation and the
template's jQuery validation provides client validation. POST actions use antiforgery tokens.

## Technology Stack

- ASP.NET Core MVC (.NET 10)
- Entity Framework Core
- SQLite
- Razor Views
- Bootstrap (included by the MVC template)

## Project Structure

- `src/RecipeManager.Web/`: the ASP.NET Core MVC application.
- `tests/`: reserved for future automated test projects.

## Domain Model

- Category 1 -> many Recipes.
- Recipe 1 -> many RecipeIngredients.
- Ingredient 1 -> many RecipeIngredients.
- RecipeIngredient connects recipes and ingredients, stores quantity and unit,
  and uses `(RecipeId, IngredientId)` as its composite primary key.

Deleting a recipe also deletes its ingredient links. A category or ingredient
that is still used cannot be deleted. The included `InitialCreate` migration
inserts these seed rows when it is first applied to a database:

- Categories: Breakfast, Main Course, Dessert, Salad.
- Ingredients: Egg, Flour, Milk, Tomato, Chicken Breast, Parmesan, Spaghetti.

Recipes can be created through the application. Seed data is defined with EF Core
`HasData` and applied by migrations, not reinserted on every application startup.

## How to Run

From the repository root, with the .NET 10 SDK installed:

```sh
dotnet restore
dotnet tool restore
dotnet build
dotnet ef database update --project src/RecipeManager.Web
dotnet run --project src/RecipeManager.Web --launch-profile http
```

Open http://localhost:5105.

Apply migrations before first use. They are not applied automatically at startup.
The connection string is in `src/RecipeManager.Web/appsettings.json`. With the
commands above, SQLite stores `recipe-manager.db` in `src/RecipeManager.Web/`.
Database files and build output are ignored by Git. The repository includes a
local `dotnet-ef` tool manifest so migration commands use the matching EF version.

## Migrations

`InitialCreate` and the model snapshot are included in
`src/RecipeManager.Web/Migrations/`. For a fresh checkout, restore the local tool
and apply the existing migration; do not create `InitialCreate` again:

```sh
dotnet tool restore
dotnet ef database update --project src/RecipeManager.Web
```

After a future intentional model change, create a new migration with a new name,
then apply it:

```sh
dotnet ef migrations add <MigrationName> --project src/RecipeManager.Web
dotnet ef database update --project src/RecipeManager.Web
```

Migration source files belong in version control; the local SQLite database does not.

## Current Scope

Recipe Create and Edit support optional local image uploads: JPG, JPEG, PNG and
WEBP, up to 5 MiB (5,242,880 bytes). Images are stored with generated filenames under
`src/RecipeManager.Web/wwwroot/uploads/recipes/`; only their relative web paths
are persisted in `Recipe.ImagePath`. Uploads are ignored by Git. Editing without a
new upload preserves the current image; replacement and recipe deletion clean up
the old local image after the database save succeeds.

Recipe ingredient links and quantity/unit are modeled, but their editor is deferred.
The final landing page, authentication, APIs and deployment are deferred.

## Publish to GitHub

From this repository root, after authenticating GitHub CLI:

```sh
gh auth login
gh repo create web-technologies-recipe-manager --private --source . --remote origin --push
```

Alternatively, create an empty repository on GitHub and use its actual URL:

```sh
git remote add origin <repository-url>
git push -u origin main
```

## Semester Tasks

- [ ] C1 MVC
- [ ] C2 Cloud
- [ ] C3 CI/CD
- [ ] C4 Docker
- [ ] C5 REST API
- [ ] QA1 Web API Testing
- [ ] C6 Bootstrap Landing Page
- [ ] C7 SEO
- [ ] B1 Pagination
- [ ] C8 External API Integration
- [ ] F1 Statistics Visualization
- [ ] F2 JavaScript / AJAX Autocomplete
- [ ] B9 API Caching
