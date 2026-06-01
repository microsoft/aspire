// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Documentation;

/// <summary>
/// Builds user-friendly cache keys from documentation source URLs.
/// </summary>
internal static class DocumentationCacheKey
{
    private const string DefaultAspireHost = "aspire.dev";

    /// <summary>
    /// Creates a friendly cache key from the specified URL.
    /// </summary>
    /// <param name="url">The source URL to convert.</param>
    /// <param name="fallbackStem">The fallback stem to use when the URL has no usable path segments.</param>
    /// <returns>A cache key that omits the default Aspire host while preserving configured-host separation.</returns>
    public static string FromUrl(string url, string fallbackStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackStem);

        var trimmedUrl = url.Trim();
        if (!Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri))
        {
            var fallback = Path.GetFileNameWithoutExtension(trimmedUrl);
            return string.IsNullOrWhiteSpace(fallback) ? fallbackStem : fallback;
        }

        var rawSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pathSegments = new List<string>(rawSegments.Length);
        for (var i = 0; i < rawSegments.Length; i++)
        {
            var segment = i == rawSegments.Length - 1
                ? Path.GetFileNameWithoutExtension(rawSegments[i])
                : rawSegments[i];

            if (!string.IsNullOrWhiteSpace(segment))
            {
                pathSegments.Add(segment);
            }
        }

        var stem = pathSegments.Count > 0 ? string.Join('-', pathSegments) : fallbackStem;
        if (uri.Host.Equals(DefaultAspireHost, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort)
        {
            return stem;
        }

        return $"{uri.Host}{(uri.IsDefaultPort ? "" : $"-{uri.Port}")}-{stem}";
    }
}
