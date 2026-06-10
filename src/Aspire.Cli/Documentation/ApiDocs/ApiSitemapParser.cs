// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;

namespace Aspire.Cli.Documentation.ApiDocs;

/// <summary>
/// Represents a single API reference route discovered from the site map.
/// </summary>
internal sealed class ApiSitemapEntry
{
    /// <summary>
    /// Gets the canonical page URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the API language for the route.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Gets the path segments that follow the language portion of the route.
    /// </summary>
    public required string[] Segments { get; init; }
}

/// <summary>
/// Parses Aspire API reference routes from the public sitemap.
/// </summary>
internal static class ApiSitemapParser
{
    private static readonly ApiLanguageRoute[] s_supportedLanguageRoutes =
    [
        new(ApiReferenceLanguages.CSharp, "/reference/api/csharp"),
        new(ApiReferenceLanguages.TypeScript, "/reference/api/typescript")
    ];

    /// <summary>
    /// Parses supported Aspire API routes from sitemap content.
    /// </summary>
    /// <param name="sitemapContent">The raw sitemap XML.</param>
    /// <returns>The parsed API routes.</returns>
    public static IReadOnlyList<ApiSitemapEntry> Parse(string sitemapContent)
    {
        if (string.IsNullOrWhiteSpace(sitemapContent))
        {
            return [];
        }

        using var stringReader = new StringReader(sitemapContent);
        using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });

        var entries = new List<ApiSitemapEntry>();
        while (xmlReader.Read())
        {
            if (xmlReader.NodeType is not XmlNodeType.Element || xmlReader.LocalName != "loc")
            {
                continue;
            }

            var location = xmlReader.ReadElementContentAsString();
            if (!TryParseApiRoute(location, out var entry))
            {
                continue;
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static bool TryParseApiRoute(string location, out ApiSitemapEntry entry)
    {
        entry = default!;

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var absolutePath = uri.AbsolutePath.AsSpan();

        foreach (var route in s_supportedLanguageRoutes)
        {
            if (!absolutePath.StartsWith(route.PathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remainder = absolutePath[route.PathPrefix.Length..];
            if (remainder.IsEmpty)
            {
                return false;
            }

            if (remainder[0] != '/')
            {
                continue;
            }

            var routeSegments = remainder[1..]
                .Trim('/')
                .ToString()
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (routeSegments.Length is 0)
            {
                return false;
            }

            entry = new ApiSitemapEntry
            {
                Url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                Language = route.Language,
                Segments = routeSegments
            };

            return true;
        }

        return false;
    }

    private readonly record struct ApiLanguageRoute(string Language, string PathPrefix);
}
