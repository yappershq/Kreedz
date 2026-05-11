var builder = WebApplication.CreateBuilder(args);

var storageRoot = Path.GetFullPath(
    builder.Configuration["StorageRoot"]
    ?? Path.Combine(AppContext.BaseDirectory, "replays"));

Directory.CreateDirectory(storageRoot);

var app = builder.Build();

app.Logger.LogInformation("Replay storage root: {Path}", storageRoot);

app.MapGet("/", () => Results.Text(
    $"Replay storage server\nRoot: {storageRoot}\n\n" +
    "PUT    /{key}   upload\n" +
    "GET    /{key}   download\n" +
    "DELETE /{key}   delete\n"));

app.MapPut("/{*key}", async (string key, HttpRequest request) =>
{
    if (!TryResolvePath(storageRoot, key, out var fullPath))
        return Results.BadRequest("invalid key");

    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

    await using var fs = File.Create(fullPath);
    await request.Body.CopyToAsync(fs);

    app.Logger.LogInformation("PUT  {Key} ({Bytes} bytes)", key, fs.Length);
    return Results.Ok(new { key, size = fs.Length });
});

app.MapGet("/{*key}", (string key) =>
{
    if (!TryResolvePath(storageRoot, key, out var fullPath))
        return Results.BadRequest("invalid key");

    if (!File.Exists(fullPath))
        return Results.NotFound();

    app.Logger.LogInformation("GET  {Key}", key);
    return Results.File(fullPath, "application/octet-stream");
});

app.MapDelete("/{*key}", (string key) =>
{
    if (!TryResolvePath(storageRoot, key, out var fullPath))
        return Results.BadRequest("invalid key");

    if (File.Exists(fullPath))
    {
        File.Delete(fullPath);
        app.Logger.LogInformation("DEL  {Key}", key);
    }

    return Results.NoContent();
});

app.Run();

static bool TryResolvePath(string root, string key, out string fullPath)
{
    fullPath = string.Empty;

    if (string.IsNullOrWhiteSpace(key))
        return false;

    var normalized = key.Replace('\\', '/').TrimStart('/');
    var combined   = Path.GetFullPath(Path.Combine(root, normalized));

    if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        && !combined.Equals(root, StringComparison.Ordinal))
    {
        return false;
    }

    fullPath = combined;
    return true;
}
