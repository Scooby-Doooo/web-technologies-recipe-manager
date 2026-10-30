using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace RecipeManager.Web.Photos;

// Where recipe photos are stored. ImagePath is a local "/uploads/recipes/<guid>.png" path or a
// public Supabase URL. Every photo gets a new name, so nothing is overwritten.
public interface IPhotoStorage
{
    // Throws PhotoStorageException when the photo could not be kept.
    Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default);

    // Best effort: a photo that cannot be removed is logged and left behind, never an error for the user.
    Task DeleteAsync(string? imagePath);
}

public class PhotoStorageException(string message, Exception? inner = null) : Exception(message, inner);

public static class PhotoFiles
{
    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp"
    };

    public static string NewName(string extension) => Guid.NewGuid().ToString("N") + extension;

    // Only names made by NewName are deleted, so a crafted ImagePath cannot point at another file.
    public static bool IsOwnName(string name) =>
        !name.Contains('/') && !name.Contains('\\')
        && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _)
        && ContentTypes.ContainsKey(Path.GetExtension(name).ToLowerInvariant());
}

// wwwroot/uploads/recipes on the server's own disk: development, tests and Docker Compose.
public class LocalPhotoStorage(IWebHostEnvironment environment, ILogger<LocalPhotoStorage> logger) : IPhotoStorage
{
    private const string Prefix = "/uploads/recipes/";
    private string Folder => Path.Combine(environment.WebRootPath, "uploads", "recipes");

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default)
    {
        var name = PhotoFiles.NewName(extension);
        var path = Path.Combine(Folder, name);
        try
        {
            Directory.CreateDirectory(Folder);
            await using var file = new FileStream(path, FileMode.CreateNew);
            await content.CopyToAsync(file, cancellationToken);
            return Prefix + name;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            throw new PhotoStorageException("Unable to save the image.", exception);
        }
    }

    public Task DeleteAsync(string? imagePath)
    {
        if (imagePath != null && imagePath.StartsWith(Prefix, StringComparison.Ordinal) && PhotoFiles.IsOwnName(imagePath[Prefix.Length..]))
            TryDelete(Path.Combine(Folder, imagePath[Prefix.Length..]));
        return Task.CompletedTask;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Unable to remove a local recipe image; manual cleanup may be needed.");
        }
    }
}

// Bound from the "Supabase" section. Without Url and SecretKey photos stay in the local folder.
public class SupabaseStorageOptions
{
    // https://<project ref>.supabase.co
    public string? Url { get; set; }

    // A secret API key (sb_secret_...): it can write to any bucket, so it lives only in user
    // secrets or environment variables and never reaches the browser.
    public string? SecretKey { get; set; }

    // A public bucket: anyone can read a photo by its URL, only the server can write.
    public string Bucket { get; set; } = "recipe-photos";

    public bool Enabled => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(SecretKey);
}

// Supabase Storage through its REST API; browsers load the photos straight from Supabase.
public class SupabasePhotoStorage(HttpClient http, IOptions<SupabaseStorageOptions> options, ILogger<SupabasePhotoStorage> logger) : IPhotoStorage
{
    private readonly SupabaseStorageOptions _options = options.Value;
    private string Api => _options.Url!.TrimEnd('/') + "/storage/v1/object/";
    private string PublicPrefix => Api + "public/" + _options.Bucket + "/";

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken cancellationToken = default)
    {
        var name = PhotoFiles.NewName(extension);
        using var request = Request(HttpMethod.Post, name);
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(PhotoFiles.ContentTypes[extension]);
        // Browsers may keep a photo for a year: a changed photo gets a new name anyway.
        request.Headers.Add("cache-control", "max-age=31536000");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new PhotoStorageException($"Supabase Storage answered {(int)response.StatusCode}.");
            return PublicPrefix + name;
        }
        catch (HttpRequestException exception)
        {
            throw new PhotoStorageException("Supabase Storage is not reachable.", exception);
        }
    }

    public async Task DeleteAsync(string? imagePath)
    {
        if (imagePath == null || !imagePath.StartsWith(PublicPrefix, StringComparison.Ordinal)) return;
        var name = imagePath[PublicPrefix.Length..];
        if (!PhotoFiles.IsOwnName(name)) return;
        try
        {
            using var request = Request(HttpMethod.Delete, name);
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Supabase Storage did not remove a recipe photo: {Status}", (int)response.StatusCode);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Unable to remove a recipe photo from Supabase Storage");
        }
    }

    // The gateway reads the secret key from the apikey header and acts as the service role.
    private HttpRequestMessage Request(HttpMethod method, string name)
    {
        var request = new HttpRequestMessage(method, Api + _options.Bucket + "/" + name);
        request.Headers.Add("apikey", _options.SecretKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
        return request;
    }
}
