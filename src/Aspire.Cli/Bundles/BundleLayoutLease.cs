// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Shared;

namespace Aspire.Cli.Bundles;

/// <summary>
/// Represents a bundle layout rooted in a stable version directory and, when applicable, its active lease.
/// </summary>
internal sealed class BundleLayoutLease : IDisposable
{
    private readonly BundleVersionLease? _lease;

    internal BundleLayoutLease(string? versionId, string? versionDirectory, LayoutConfiguration layout, BundleVersionLease? lease)
    {
        VersionId = versionId;
        VersionDirectory = versionDirectory;
        Layout = layout;
        _lease = lease;
    }

    /// <summary>
    /// Gets the resolved bundle version id, or <see langword="null"/> for unleased fallback layouts.
    /// </summary>
    public string? VersionId { get; }

    /// <summary>
    /// Gets the resolved bundle version directory, or <see langword="null"/> for unleased fallback layouts.
    /// </summary>
    public string? VersionDirectory { get; }

    /// <summary>
    /// Gets the version-rooted layout configuration.
    /// </summary>
    public LayoutConfiguration Layout { get; }

    /// <summary>
    /// Gets whether this result holds an active version lease.
    /// </summary>
    public bool HasLease => _lease is not null;

    /// <summary>
    /// Adds lease handoff environment variables for a bundle-owned child process.
    /// </summary>
    public void AddEnvironment(IDictionary<string, string> environmentVariables)
    {
        if (VersionDirectory is not null)
        {
            BundleVersionLease.AddEnvironment(environmentVariables, VersionDirectory);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _lease?.Dispose();
    }
}
