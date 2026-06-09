// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestSelector.Analyzers;

/// <summary>
/// Applies the <c>inferDeps</c> rule: a test project whose <c>inferDeps</c> entry is
/// <see langword="false"/> runs ONLY when an explicit edge (a <c>mappings</c> or <c>edges</c>
/// entry) resolved to it — never merely because <c>dotnet-affected</c> inferred a transitive
/// edge from the build graph.
/// </summary>
/// <remarks>
/// This is intentionally a free function so it can be unit-tested in isolation.
/// <see cref="TestEvaluator"/> calls it from inside <c>FilterAndCombineTestProjects</c> after
/// merging mapping + dotnet-affected results. The semantics:
/// <list type="bullet">
///   <item>
///     A project with <c>inferDeps[path] == false</c> pulled in only by dotnet-affected
///     (transitive reference, package dependency) is dropped — the opt-in is the explicit edge,
///     not the inferred build-graph reference.
///   </item>
///   <item>
///     A project with <c>inferDeps[path] == false</c> that a declared edge resolved to is kept —
///     that edge is the explicit opt-in.
///   </item>
///   <item>
///     Projects with no entry, or <c>inferDeps[path] == true</c>, pass through unchanged
///     (the default is to trust the inferred edges).
///   </item>
/// </list>
/// </remarks>
public static class InferDepsFilter
{
    /// <summary>
    /// Returns <paramref name="allTestProjects"/> with <c>inferDeps:false</c> projects that were
    /// not resolved by a declared edge removed. Comparisons are case-insensitive and tolerate
    /// backslash separators.
    /// </summary>
    /// <param name="allTestProjects">Merged test project list (mapping + dotnet-affected).</param>
    /// <param name="inferDeps">The <c>inferDeps</c> map; entries absent or <see langword="true"/> are no-ops.</param>
    /// <param name="declaredProjects">Projects resolved by a declared <c>mappings</c>/<c>edges</c> entry.</param>
    /// <returns>Filtered list preserving original order.</returns>
    public static List<string> Apply(
        IReadOnlyList<string> allTestProjects,
        IReadOnlyDictionary<string, bool> inferDeps,
        IReadOnlyList<string> declaredProjects)
    {
        // Only entries explicitly set to false suppress anything; absent/true entries are no-ops.
        var suppressed = inferDeps
            .Where(kvp => kvp.Value == false)
            .Select(kvp => Normalize(kvp.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (suppressed.Count == 0)
        {
            return allTestProjects.ToList();
        }

        var declared = new HashSet<string>(
            declaredProjects.Select(Normalize),
            StringComparer.OrdinalIgnoreCase);

        return allTestProjects
            .Where(p =>
            {
                var n = Normalize(p);
                return !suppressed.Contains(n) || declared.Contains(n);
            })
            .ToList();
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
