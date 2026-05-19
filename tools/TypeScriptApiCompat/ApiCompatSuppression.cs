// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace TypeScriptApiCompat;

internal sealed record ApiCompatSuppression(
    string Kind,
    string PackageName,
    string Symbol,
    string Url,
    string Reason,
    string FilePath,
    int LineNumber)
{
    public string SuppressionKey => $"{Kind}|{PackageName}|{Symbol}";
}

internal sealed record SuppressionLoadResult(
    IReadOnlyList<ApiCompatSuppression> Suppressions,
    IReadOnlyList<string> Errors);

internal static partial class ApiCompatSuppressionLoader
{
    public static SuppressionLoadResult Load(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return new SuppressionLoadResult([], [$"Suppressions root '{rootPath}' does not exist."]);
        }

        var suppressions = new List<ApiCompatSuppression>();
        var errors = new List<string>();
        var files = EnumerateSuppressionFiles(rootPath).Order(StringComparer.Ordinal);

        foreach (var file in files)
        {
            ParseFile(file, suppressions, errors);
        }

        var duplicateGroups = suppressions
            .GroupBy(static suppression => suppression.SuppressionKey, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1);

        foreach (var group in duplicateGroups)
        {
            var locations = string.Join(", ", group.Select(static suppression => $"{suppression.FilePath}:{suppression.LineNumber}"));
            errors.Add($"Duplicate suppression '{group.Key}' appears at {locations}.");
        }

        return new SuppressionLoadResult(suppressions, errors);
    }

    private static void ParseFile(string filePath, List<ApiCompatSuppression> suppressions, List<string> errors)
    {
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(filePath))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var match = SuppressionLineRegex().Match(line);
            if (!match.Success)
            {
                errors.Add($"{filePath}:{lineNumber}: Invalid suppression. Expected: BREAK <kind> <package> <symbol> -- <issue-or-pr-url> -- <reason>");
                continue;
            }

            var url = match.Groups["url"].Value;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                errors.Add($"{filePath}:{lineNumber}: Suppression URL must be an absolute http(s) URL.");
                continue;
            }

            var reason = match.Groups["reason"].Value.Trim();
            if (reason.Length == 0)
            {
                errors.Add($"{filePath}:{lineNumber}: Suppression reason is required.");
                continue;
            }

            suppressions.Add(new ApiCompatSuppression(
                match.Groups["kind"].Value,
                match.Groups["package"].Value,
                match.Groups["symbol"].Value.Trim(),
                url,
                reason,
                filePath,
                lineNumber));
        }
    }

    private static IEnumerable<string> EnumerateSuppressionFiles(string rootPath)
    {
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootPath);

        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.EndsWith(".tscompat.suppression.txt", StringComparison.Ordinal) ||
                    string.Equals(fileName, "global.suppression.txt", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(childDirectory);
                if (name is ".git" or ".dotnet" or "artifacts" or "bin" or "obj" or "node_modules")
                {
                    continue;
                }

                pendingDirectories.Push(childDirectory);
            }
        }
    }

    [GeneratedRegex(@"^BREAK\s+(?<kind>\S+)\s+(?<package>\S+)\s+(?<symbol>.+?)\s+--\s+(?<url>\S+)\s+--\s+(?<reason>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SuppressionLineRegex();
}
