// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Bundles;

/// <summary>
/// Manages extraction of the embedded bundle payload from self-extracting CLI binaries.
/// </summary>
internal interface IBundleService
{
    /// <summary>
    /// Gets whether the current CLI binary contains an embedded bundle payload.
    /// </summary>
    bool IsBundle { get; }

    /// <summary>
    /// Ensures the bundle is extracted for the current CLI binary if it contains an embedded payload.
    /// No-ops if no payload is embedded, or if the layout is already extracted and up to date.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task EnsureExtractedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the bundle payload to the specified directory.
    /// </summary>
    /// <param name="destinationPath">Directory to extract into.</param>
    /// <param name="force">If true, re-extract even if the version matches.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the extraction attempt.</returns>
    Task<BundleExtractResult> ExtractAsync(string destinationPath, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures the bundle is extracted and returns a version-rooted layout with a lease that prevents cleanup.
    /// Extraction, active-version resolution, and lease acquisition happen under the same bundle lock
    /// so cleanup cannot delete the selected version before the lease protects it.
    /// Callers that start bundle-owned processes should use this method and keep the returned lease alive
    /// until the child process has exited or acquired its own lease.
    /// </summary>
    /// <param name="holderKind">Diagnostic category for the lease holder.</param>
    /// <param name="commandName">Optional command name for diagnostics.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The leased layout, or <see langword="null"/> if no layout is found.</returns>
    Task<BundleLayoutLease?> EnsureExtractedAndAcquireLayoutAsync(string holderKind, string? commandName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines the default extraction directory for the supplied CLI binary path
    /// by reading the <c>.aspire-install.json</c> sidecar (if any) next to the
    /// resolved binary and switching on its <c>source</c> field. The resulting
    /// directory is the parent of <c>versions/&lt;id&gt;/</c> and varies by source:
    /// <list type="bullet">
    /// <item><description>Script / PR sources (<c>source=script</c> or <c>source=pr</c>,
    /// binary in <c>bin/</c>): the parent of the binary's directory (= the install
    /// prefix root).</description></item>
    /// <item><description>Packager-managed sources (<c>source=winget</c> /
    /// <c>source=brew</c> / <c>source=dotnet-tool</c>): the directory containing the
    /// binary (symlinks resolved first).</description></item>
    /// <item><description>No sidecar / unmanaged installs: the parent of the binary's
    /// directory, preserving the historical <c>~/.aspire/bin/aspire → ~/.aspire/</c>
    /// heuristic.</description></item>
    /// </list>
    /// </summary>
    /// <param name="processPath">An absolute path to the CLI binary.</param>
    /// <returns>The extraction directory, or <see langword="null"/> if it cannot be determined.</returns>
    string? GetDefaultExtractDir(string processPath);
}

/// <summary>
/// Result of a bundle extraction attempt.
/// </summary>
internal enum BundleExtractResult
{
    /// <summary>No embedded payload found in the binary.</summary>
    NoPayload,

    /// <summary>Layout already exists and version matches — extraction skipped.</summary>
    AlreadyUpToDate,

    /// <summary>Extraction completed successfully.</summary>
    Extracted,

    /// <summary>Extraction completed but layout validation failed.</summary>
    ExtractionFailed
}
