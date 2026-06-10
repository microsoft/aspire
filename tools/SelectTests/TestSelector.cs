// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.SelectTests;

/// <summary>
/// Options that override normal selection.
/// </summary>
/// <param name="ForceAll">
/// The kill switch: a <c>[full ci]</c> token in the PR or a <c>run-all-tests</c> label forces the
/// whole matrix to run regardless of which files changed.
/// </param>
public sealed record SelectorOptions(bool ForceAll = false);

/// <summary>
/// The outcome of selecting which CI work to run for a set of changed files.
/// </summary>
/// <param name="SelectsAll">
/// True when the whole test matrix must run (a <c>run_all_globs</c> match, a fail-open escalation,
/// or the kill switch). When true, <see cref="TestProjects"/> is the full matrix.
/// </param>
/// <param name="TestProjects">The selected test project names (matrix <c>projectName</c>), aliases expanded.</param>
/// <param name="Jobs">The selected non-.NET jobs (e.g. <c>job:polyglot</c>, <c>job:extension-e2e</c>).</param>
/// <param name="EscalationReason">When <see cref="SelectsAll"/> is true, a short human-readable reason.</param>
public sealed record SelectionResult(
    bool SelectsAll,
    IReadOnlySet<string> TestProjects,
    IReadOnlySet<string> Jobs,
    string? EscalationReason);

/// <summary>
/// Filters the full CI matrix down to the subset relevant to a PR's changed files, using the
/// curated <c>docs/ci/test-trigger-map.yml</c> (Layer 2) unioned with a graph-derived affected set
/// (Layer 1, e.g. from <c>dotnet-affected</c>, supplied to <see cref="Select"/>).
/// </summary>
/// <remarks>
/// Behavior is specified by the acceptance tests in
/// <c>Infrastructure.Tests/TestTriggerMap/SelectTestsAcceptanceTests.cs</c>. The resolution logic
/// is not implemented yet; <see cref="Select"/> throws until it is.
/// </remarks>
public sealed class TestSelector
{
    private readonly string _mapPath;
    private readonly IReadOnlyCollection<string> _allTestProjects;

    /// <param name="mapPath">Path to <c>docs/ci/test-trigger-map.yml</c>.</param>
    /// <param name="allTestProjects">All matrix test project names — the universe an <c>ALL</c> selection expands to.</param>
    public TestSelector(string mapPath, IReadOnlyCollection<string> allTestProjects)
    {
        _mapPath = mapPath;
        _allTestProjects = allTestProjects;
    }

    /// <param name="changedFiles">Repo-relative, '/'-separated paths changed in the PR.</param>
    /// <param name="layer1Affected">
    /// Test projects reported by the graph tool (the union of its <em>changed</em> and
    /// <em>affected</em> sets). May be empty.
    /// </param>
    /// <param name="options">Selection overrides (kill switch).</param>
    public SelectionResult Select(
        IReadOnlyCollection<string> changedFiles,
        IReadOnlyCollection<string> layer1Affected,
        SelectorOptions options)
        => throw new NotImplementedException(
            "SelectTests resolution is not implemented yet. See the acceptance tests in " +
            "Infrastructure.Tests/TestTriggerMap/SelectTestsAcceptanceTests.cs for the expected behavior.");
}
