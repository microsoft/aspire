// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Catalog of build sources the user can pick from when configuring a
/// dogfooding session — open PRs, the daily build, the staging build, etc.
/// Phase 1 ships <see cref="StubPrCatalog"/> with hand-coded entries so the
/// UI is exercisable; Phase 5 will replace it with a real GitHub API client
/// that uses the token cached during environment validation.
/// </summary>
internal interface IPrCatalog
{
    Task<IReadOnlyList<PrCatalogEntry>> ListAsync(CancellationToken cancellationToken);
}

/// <param name="Number">PR number on <c>microsoft/aspire</c>.</param>
/// <param name="Title">PR title for display.</param>
/// <param name="HasBuiltArtifacts">Whether the PR build pipeline has produced testable artifacts. Surfaced so the picker can hide PRs that aren't dogfoodable yet.</param>
internal sealed record PrCatalogEntry(int Number, string Title, bool HasBuiltArtifacts);

internal sealed class StubPrCatalog : IPrCatalog
{
    public Task<IReadOnlyList<PrCatalogEntry>> ListAsync(CancellationToken cancellationToken)
    {
        // Hand-coded placeholder content for Phase 1 — purely to give the
        // SessionConfigPanel something to render when the user picks the
        // "pr-<N>" channel. Replaced wholesale in Phase 5.
        IReadOnlyList<PrCatalogEntry> entries =
        [
            new(11111, "Stub: example open PR (Phase 5 will list real PRs)", true),
        ];
        return Task.FromResult(entries);
    }
}
