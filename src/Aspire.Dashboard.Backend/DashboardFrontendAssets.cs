// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Dashboard.Backend;

internal interface IDashboardFrontendAssetProvider
{
    Stream? Open(string path);
}

internal sealed class EmbeddedDashboardFrontendAssetProvider : IDashboardFrontendAssetProvider
{
    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> s_assets = new(LoadAssets);

    public Stream? Open(string path)
    {
        if (path.Length == 0 || path[0] == '/' || path.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return s_assets.Value.TryGetValue(path, out var content)
            ? new MemoryStream(content, writable: false)
            : null;
    }

    private static IReadOnlyDictionary<string, byte[]> LoadAssets()
    {
        using var archiveStream = typeof(EmbeddedDashboardFrontendAssetProvider).Assembly
            .GetManifestResourceStream("DashboardFrontend.zip")
            ?? throw new InvalidOperationException("The published dashboard is missing its embedded React frontend.");
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            using var content = entry.Open();
            using var buffer = new MemoryStream(checked((int)entry.Length));
            content.CopyTo(buffer);
            assets.Add(entry.FullName, buffer.ToArray());
        }

        return assets;
    }
}

internal static partial class DashboardFrontendAssets
{
    private const string BackendMarker = "<meta name=\"aspire-dashboard-backend\" content=\"standalone\" />";
    private const string HostedBackendMarker = "<meta name=\"aspire-dashboard-backend\" content=\"aot\" />";

    public static void Map(WebApplication app)
    {
        app.MapGet("/{**path}", async (string? path, HttpContext context, IDashboardFrontendAssetProvider assets) =>
        {
            var normalizedPath = string.IsNullOrEmpty(path) ? "index.html" : path;
            if (normalizedPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var asset = assets.Open(normalizedPath);
            if (asset is null && !Path.HasExtension(normalizedPath))
            {
                normalizedPath = "index.html";
                asset = assets.Open(normalizedPath);
            }
            if (asset is null)
            {
                return Results.NotFound();
            }

            if (string.Equals(normalizedPath, "index.html", StringComparison.Ordinal))
            {
                await using (asset)
                using (var reader = new StreamReader(asset, Encoding.UTF8))
                {
                    var html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
                    if (!html.Contains(BackendMarker, StringComparison.Ordinal))
                    {
                        return Results.Problem("The embedded dashboard index is missing its backend marker.");
                    }

                    context.Response.Headers.CacheControl = "no-cache";
                    html = html.Replace("=\"./", "=\"/", StringComparison.Ordinal);
                    return Results.Text(
                        html.Replace(BackendMarker, HostedBackendMarker, StringComparison.Ordinal),
                        "text/html; charset=utf-8");
                }
            }

            context.Response.Headers.CacheControl = HashedAssetRegex().IsMatch(normalizedPath)
                ? "public,max-age=31536000,immutable"
                : "no-cache";
            return Results.Stream(asset, GetContentType(normalizedPath));
        });
    }

    private static string GetContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".css" => "text/css; charset=utf-8",
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    [GeneratedRegex(@"(?:^|/)[^/]+-[A-Za-z0-9_-]{8,}\.[^/]+$")]
    private static partial Regex HashedAssetRegex();
}
