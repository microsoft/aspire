// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dogfooder.Scenarios.Catalog;

namespace Aspire.Dogfooder.Scenarios;

/// <summary>
/// Catalog of all built-in <see cref="IDogfoodScenario"/> implementations.
/// Looked up by scenario id so persisted sessions can re-resolve their
/// scenario on app restart.
/// </summary>
/// <remarks>
/// The default ordering of <see cref="Scenarios"/> is the ordering the
/// scenario picker renders, so it doubles as the UI's "most useful first"
/// ranking. Reproduce-vCurrent variants are first because they are the
/// most common dogfooding entry point.
/// </remarks>
internal sealed class DogfoodScenarioRegistry
{
    public DogfoodScenarioRegistry()
    {
        // Cache the repo version once. Scenarios that depend on it (most of
        // them) close over this instance so they reflect the repo's version
        // at app launch rather than walking the filesystem on every Build().
        var repoVersion = RepoVersionInfo.Load();
        RepoVersion = repoVersion;

        Scenarios = new IDogfoodScenario[]
        {
            new ReproVCurrentLocalScenario(repoVersion),
            new ReproVCurrentPublishedScenario(repoVersion),
            new VNextMinorStableLocalScenario(repoVersion),
            new VNextHotfixStableLocalScenario(repoVersion),
            new VNextPrLocalScenario(repoVersion),
            new VNextDailyLocalScenario(repoVersion),
            new VNextStagingLocalScenario(repoVersion),
        };

        _byId = Scenarios.ToDictionary(s => s.Id, StringComparer.Ordinal);
    }

    private readonly Dictionary<string, IDogfoodScenario> _byId;

    public IReadOnlyList<IDogfoodScenario> Scenarios { get; }

    public RepoVersionInfo RepoVersion { get; }

    /// <summary>
    /// The scenario the UI selects by default when creating a new session,
    /// and the fallback when a persisted scenario id no longer resolves.
    /// </summary>
    public IDogfoodScenario Default => Scenarios[0];

    public bool TryGet(string scenarioId, out IDogfoodScenario scenario) =>
        _byId.TryGetValue(scenarioId, out scenario!);

    public IDogfoodScenario GetOrDefault(string? scenarioId)
    {
        if (scenarioId is { Length: > 0 } id && _byId.TryGetValue(id, out var s))
        {
            return s;
        }
        return Default;
    }
}
