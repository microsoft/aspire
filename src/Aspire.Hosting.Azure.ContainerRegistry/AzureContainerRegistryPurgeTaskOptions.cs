// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Options for a scheduled Azure Container Registry purge task.
/// </summary>
/// <remarks>
/// Tag removal alone does not reclaim image storage. Enable <see cref="Untagged"/> to also delete
/// eligible untagged manifests. Image layers shared with retained manifests remain in the registry.
/// </remarks>
[AspireDto]
public sealed class AzureContainerRegistryPurgeTaskOptions
{
    /// <summary>
    /// Gets or initializes the repository and tag regular expressions, separated by a colon.
    /// Defaults to <c>".*:.*"</c> (all repositories and tags) when <see langword="null"/>.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Gets or initializes the age threshold based on the artifact's last modified time.
    /// Defaults to zero (all ages) when <see langword="null"/>.
    /// Non-zero values must be at least one minute.
    /// </summary>
    public TimeSpan? Ago { get; init; }

    /// <summary>
    /// Gets or initializes the number of most recent purge candidates to retain per repository.
    /// Defaults to 3 and must be greater than zero.
    /// </summary>
    /// <remarks>
    /// When <see cref="Untagged"/> is enabled, the purge tool applies this count independently
    /// to tags and untagged manifests. This is not a count of deployable application versions.
    /// </remarks>
    public int Keep { get; init; } = 3;

    /// <summary>
    /// Gets or initializes the task name. A unique name is generated when not specified.
    /// </summary>
    public string? TaskName { get; init; }

    /// <summary>
    /// Gets or initializes whether to delete eligible untagged manifests in addition to tags.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// This also selects previously untagged manifests in repositories matched by <see cref="Filter"/>,
    /// not just manifests untagged by this task. Deletion is irreversible and can break digest-based
    /// image pulls and rollbacks. No Premium SKU or retention policy is required.
    /// The purge tool's version determines how age and keep filters apply to untagged manifests.
    /// See <see href="https://learn.microsoft.com/azure/container-registry/container-registry-auto-purge"/>.
    /// </remarks>
    public bool Untagged { get; init; }

    /// <summary>
    /// Gets or initializes whether to log the artifacts that would be deleted without deleting them.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool DryRun { get; init; }
}
