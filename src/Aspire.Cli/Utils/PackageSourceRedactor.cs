// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Helpers for rendering NuGet package sources in user-visible output without leaking credentials.
/// </summary>
internal static class PackageSourceRedactor
{
    private const string UnparseableHttpSentinel = "<unparseable http source>";

    /// <summary>
    /// Returns a display-safe form of a NuGet source for inclusion in user-visible output (error
    /// footers, debug logs, bug reports). For http/https feeds we strip the UserInfo, query, and
    /// fragment because users commonly pass <c>https://user:pat@host/...</c> or SAS-token URLs
    /// (<c>?sv=...&amp;sig=...</c>). Local paths and other source forms (file://, bare paths on
    /// Windows/Unix) pass through unchanged — they don't carry credentials.
    /// </summary>
    /// <remarks>
    /// Fails closed for HTTP-shaped inputs that <see cref="Uri.TryCreate(string, UriKind, out Uri)"/>
    /// cannot parse (for example <c>https://user:p@ss@host/path</c> or
    /// <c>https://user:p#word@host/</c>): returns a sentinel rather than the raw input. Leading
    /// and trailing whitespace is ignored for HTTP detection so indented feed URLs are still
    /// protected. Plain non-HTTP-looking inputs (local paths, file://, etc.) still pass through
    /// unchanged.
    /// </remarks>
    public static string RedactForDisplay(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source;
        }

        var sourceToParse = source.Trim();
        if (sourceToParse.Length == 0)
        {
            return source;
        }

        // Detect HTTP-shaped inputs before attempting to parse so malformed URLs that look like
        // an HTTP feed fail closed instead of leaking credentials through the parse-failure
        // branch below. Trim first because NuGet sources in config/output can be indented.
        var looksHttp =
            sourceToParse.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            sourceToParse.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(sourceToParse, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return looksHttp ? UnparseableHttpSentinel : source;
        }

        var hasUserInfo = !string.IsNullOrEmpty(uri.UserInfo);
        var hasQuery = !string.IsNullOrEmpty(uri.Query);
        var hasFragment = !string.IsNullOrEmpty(uri.Fragment);
        if (!hasUserInfo && !hasQuery && !hasFragment)
        {
            return sourceToParse;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = hasUserInfo ? "***" : string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.ToString();
    }

    /// <summary>
    /// Replaces credential-bearing package source occurrences in captured process output with
    /// display-safe forms.
    /// </summary>
    public static string RedactOccurrences(string value, IReadOnlyList<string> sensitiveSources)
    {
        var replacements = sensitiveSources
            .SelectMany(static source => GetDiagnosticSpellings(source)
                .Select(spelling => (Spelling: spelling, Replacement: RedactForDisplay(source))))
            .DistinctBy(static replacement => replacement.Spelling, StringComparer.Ordinal)
            .OrderByDescending(static replacement => replacement.Spelling.Length);

        foreach (var (spelling, replacement) in replacements)
        {
            value = value.Replace(spelling, replacement, StringComparison.Ordinal);
        }

        return value;
    }

    private static IEnumerable<string> GetDiagnosticSpellings(string source)
    {
        if (source.Length > 0)
        {
            yield return source;
        }

        var trimmedSource = source.Trim();
        if (trimmedSource.Length == 0)
        {
            yield break;
        }

        yield return trimmedSource;

        // NuGet diagnostics can render the parsed URI rather than the original configuration text.
        // For example, `HTTPS://user:secret@HOST/Feed?sig=secret` can be emitted as
        // `https://user:secret@host/Feed?sig=secret`.
        if (Uri.TryCreate(trimmedSource, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            yield return uri.AbsoluteUri;
        }
    }
}
