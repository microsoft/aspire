// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides the runtime data used to create a launch configuration for a resource.
/// </summary>
/// <remarks>
/// Aspire creates a new context when the resource's active debug-support annotation produces a launch
/// configuration for an executable creation, including restarts and replicas. The producer is not invoked
/// when the annotation is inactive, unsupported by the current debug session, or skipped because a
/// <see cref="ProjectLaunchArgsOverrideAnnotation"/> already supplied a <see cref="KnownLaunchConfigurationTypes.Project"/>
/// launch configuration.
/// <see cref="OriginalExecutionConfiguration"/> contains the resolved resource configuration before an active
/// debug-support argument rewrite runs. <see cref="ExecutableExecutionConfiguration"/> contains the copy used to
/// populate the underlying executable after that rewrite. When a <see cref="ProjectLaunchArgsOverrideAnnotation"/>
/// pins a project executable to process execution, the debug argument rewrite is suppressed so the process command
/// line remains runnable. Only the launch configuration returned by the producer is serialized for the IDE.
/// Processed environment values can contain secrets.
/// </remarks>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class LaunchConfigurationCallbackContext
{
    internal LaunchConfigurationCallbackContext(
        string mode,
        IResource resource,
        IExecutionConfigurationResult originalExecutionConfiguration,
        IExecutionConfigurationResult executableExecutionConfiguration,
        DistributedApplicationExecutionContext executionContext,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(originalExecutionConfiguration);
        ArgumentNullException.ThrowIfNull(executableExecutionConfiguration);
        ArgumentNullException.ThrowIfNull(executionContext);

        Mode = mode;
        Resource = resource;
        OriginalExecutionConfiguration = originalExecutionConfiguration;
        ExecutableExecutionConfiguration = executableExecutionConfiguration;
        ExecutionContext = executionContext;
        Logger = logger ?? NullLogger.Instance;
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
    /// Gets the resolved execution configuration before the active debug-support argument rewrite runs.
    /// </summary>
    /// <remarks>
    /// Processed environment values can contain secrets. Aspire serializes only the launch configuration
    /// returned by the producer; integrations should copy values from this result only when the IDE requires them.
    /// </remarks>
    public IExecutionConfigurationResult OriginalExecutionConfiguration { get; }

    /// <summary>
    /// Gets the resolved execution configuration used to populate the executable after the active debug-support argument rewrite runs.
    /// </summary>
    /// <remarks>
    /// This is a copy of <see cref="OriginalExecutionConfiguration"/> with the active <c>argsCallback</c> applied.
    /// When debug support does not rewrite arguments, or a project launch-args override keeps the executable in
    /// process mode, this is the same instance as <see cref="OriginalExecutionConfiguration"/>.
    /// </remarks>
    public IExecutionConfigurationResult ExecutableExecutionConfiguration { get; }

    /// <summary>
    /// Gets the execution context for the current AppHost invocation.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; }

    /// <summary>
    /// Gets the resource logger for this executable creation.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Gets the cancellation token for this executable creation.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}
