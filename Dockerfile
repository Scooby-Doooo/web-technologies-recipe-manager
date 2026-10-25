# Build stage: restore and publish with the full .NET SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first so this layer is cached until the project file changes.
COPY src/RecipeManager.Web/RecipeManager.Web.csproj src/RecipeManager.Web/
RUN dotnet restore src/RecipeManager.Web/RecipeManager.Web.csproj

COPY src/ src/
RUN dotnet publish src/RecipeManager.Web/RecipeManager.Web.csproj \
    --configuration Release --no-restore --output /app

# Runtime stage: only the ASP.NET Core runtime and the published app.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Uploaded photos live in a directory that compose mounts as a volume. The database is PostgreSQL:
# the db service of compose, or Supabase in the cloud, given by ConnectionStrings__DefaultConnection.
RUN mkdir -p /app/wwwroot/uploads/recipes \
    && chown -R $APP_UID /app/wwwroot/uploads
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Database__MigrateOnStartup=true
EXPOSE 8080

ENTRYPOINT ["dotnet", "RecipeManager.Web.dll"]
