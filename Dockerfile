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

# SQLite database and uploaded photos live in directories that compose mounts as volumes.
RUN mkdir -p /app/data /app/wwwroot/uploads/recipes \
    && chown -R $APP_UID /app/data /app/wwwroot/uploads
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__DefaultConnection="Data Source=/app/data/recipe-manager.db" \
    Database__MigrateOnStartup=true \
    DataProtection__KeysPath=/app/data/keys
EXPOSE 8080

ENTRYPOINT ["dotnet", "RecipeManager.Web.dll"]
