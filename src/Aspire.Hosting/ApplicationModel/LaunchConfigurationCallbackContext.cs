// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides the runtime data used to create a launch configuration for a resource.
/// </summary>
/// <remarks>
/// Aspire creates this context after resolving the execution configuration for a specific executable
/// creation. Environment variable values may contain secrets; only copy values into the launch
/// configuration when the IDE requires them.
/// </remarks>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class LaunchConfigurationCallbackContext
{
    internal LaunchConfigurationCallbackContext(
        string mode,
        IResource resource,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(environmentVariables);

        Mode = mode;
        Resource = resource;
        EnvironmentVariables = environmentVariables;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the requested launch mode, one of the values on <see cref="ExecutableLaunchMode"/>.
    /// </summary>
    public string Mode { get; }

    /// <summary>
    /// Gets the resource being launched.
    /// </summary>
    public IResource Resource { get; }

    /// <summary>
    /// Gets the resolved environment variables used for this executable creation.
    /// </summary>
    /// <remarks>
    /// Values can contain secrets. Aspire serializes only the launch configuration returned by the
    /// producer; integrations should copy only values required by the IDE.
    /// </remarks>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; }

    /// <summary>
    /// Gets the cancellation token for this executable creation.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
