// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.GitHub;

/// <summary>
/// Records that a resource references a GitHub App, and the environment variables the reference gives it.
/// </summary>
/// <remarks>
/// The names let a later reference detect that it would overwrite the variables of a different app.
/// </remarks>
internal sealed class GitHubAppReferenceAnnotation(GitHubAppResource source, string? connectionName, IReadOnlyCollection<string> environmentVariableNames) : IResourceAnnotation
{
    public GitHubAppResource Source { get; } = source;

    // The connection name as the caller wrote it, or null for the default names, for diagnostics.
    public string? ConnectionName { get; } = connectionName;

    public IReadOnlyCollection<string> EnvironmentVariableNames { get; } = environmentVariableNames;
}
