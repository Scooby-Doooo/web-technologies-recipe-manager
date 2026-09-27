# Recipe Manager

Semester project for the Web Technologies course.

## Description

Recipe Manager is a simple ASP.NET Core MVC web application for managing recipes,
recipe categories and ingredients. The project is developed gradually as a single MVC application.

## Technology Stack

- ASP.NET Core MVC (.NET 10)
- Razor Views
- Bootstrap (included by the MVC template)

## Project Structure

- `src/RecipeManager.Web/`: the ASP.NET Core MVC application.
- `tests/`: reserved for future automated test projects.

## How to Run

From the repository root, with the .NET 10 SDK installed:

```sh
dotnet restore
dotnet build
dotnet run --project src/RecipeManager.Web --launch-profile http
```

Open http://localhost:5105.

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
