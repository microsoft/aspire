// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Cli.Npm;

/// <summary>
/// Reads published package metadata from the public npm registry.
/// </summary>
internal interface INpmRegistryClient
{
    /// <summary>
    /// Gets the version currently pointed at by the package's <c>latest</c> dist-tag.
    /// </summary>
    /// <param name="packageName">The npm package name (e.g., "@microsoft/aspire-cli").</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The version that <c>&lt;packageName&gt;@latest</c> currently resolves to.</returns>
    Task<SemVersion> GetLatestVersionAsync(string packageName, CancellationToken cancellationToken);
}
