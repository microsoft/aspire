// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TypeScriptApiCompat;

internal sealed record ApiCompatResult(
    IReadOnlyList<ApiCompatDiagnostic> UnsuppressedDiagnostics,
    IReadOnlyList<ApiCompatDiagnostic> SuppressedDiagnostics,
    IReadOnlyList<ApiCompatSuppression> UnusedSuppressions,
    IReadOnlyList<string> SuppressionErrors)
{
    public bool HasFailures =>
        UnsuppressedDiagnostics.Count > 0 ||
        UnusedSuppressions.Count > 0 ||
        SuppressionErrors.Count > 0;
}

internal static class ApiCompatSuppressor
{
    public static ApiCompatResult ApplySuppressions(
        IReadOnlyList<ApiCompatDiagnostic> diagnostics,
        SuppressionLoadResult suppressionLoadResult,
        SuppressionLoadResult? baselineSuppressionLoadResult = null)
    {
        var suppressionsByKey = suppressionLoadResult.Suppressions
            .GroupBy(static suppression => suppression.SuppressionKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var baselineSuppressionKeys = baselineSuppressionLoadResult?.Suppressions
            .Select(static suppression => suppression.SuppressionKey)
            .ToHashSet(StringComparer.Ordinal);
        var usedSuppressionKeys = new HashSet<string>(StringComparer.Ordinal);
        var unsuppressedDiagnostics = new List<ApiCompatDiagnostic>();
        var suppressedDiagnostics = new List<ApiCompatDiagnostic>();

        foreach (var diagnostic in diagnostics)
        {
            if (suppressionsByKey.ContainsKey(diagnostic.SuppressionKey))
            {
                usedSuppressionKeys.Add(diagnostic.SuppressionKey);
                suppressedDiagnostics.Add(diagnostic);
            }
            else
            {
                unsuppressedDiagnostics.Add(diagnostic);
            }
        }

        var unusedSuppressions = suppressionLoadResult.Suppressions
            .Where(suppression => !usedSuppressionKeys.Contains(suppression.SuppressionKey))
            .Where(suppression => baselineSuppressionKeys is null || !baselineSuppressionKeys.Contains(suppression.SuppressionKey))
            .OrderBy(static suppression => suppression.FilePath, StringComparer.Ordinal)
            .ThenBy(static suppression => suppression.LineNumber)
            .ToArray();

        return new ApiCompatResult(
            unsuppressedDiagnostics,
            suppressedDiagnostics,
            unusedSuppressions,
            suppressionLoadResult.Errors);
    }
}
