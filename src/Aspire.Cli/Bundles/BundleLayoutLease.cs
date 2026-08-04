// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Layout;
using Aspire.Shared;

namespace Aspire.Cli.Bundles;

/// <summary>
/// Represents a bundle layout rooted in a stable version directory and, when applicable, its active lease.
/// </summary>
internal sealed class BundleLayoutLease : IDisposable
{
    private readonly BundleVersionLease? _lease;

    internal BundleLayoutLease(LayoutConfiguration layout, BundleVersionLease? lease)
    {
        Layout = layout;
        _lease = lease;
    }

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
        => _lease?.AddEnvironment(environmentVariables);

    /// <summary>
    /// Adds lease handoff environment variables to a child process.
    /// </summary>
    public void AddEnvironment(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (_lease is not null)
        {
            startInfo.Environment[BundleDiscovery.BundleVersionDirectoryEnvVar] = _lease.VersionDirectory;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _lease?.Dispose();
    }
}
