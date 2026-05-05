// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a callback context for environment variables associated with a publisher.
/// </summary>
/// <param name="executionContext">The execution context for this invocation of the AppHost.</param>
/// <param name="environmentVariables">The environment variables associated with this execution.</param>
/// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
[AspireExport]
public class EnvironmentCallbackContext(DistributedApplicationExecutionContext executionContext, Dictionary<string, object>? environmentVariables = null, CancellationToken cancellationToken = default)
{
    private readonly IResource? _resource;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentCallbackContext"/> class.
    /// </summary>
    /// <param name="executionContext">The execution context for this invocation of the AppHost.</param>
    /// <param name="resource">The resource associated with this callback context.</param>
    /// <param name="environmentVariables">The environment variables associated with this execution.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
    public EnvironmentCallbackContext(DistributedApplicationExecutionContext executionContext, IResource resource, Dictionary<string, object>? environmentVariables = null, CancellationToken cancellationToken = default)
        : this(executionContext, environmentVariables, cancellationToken)
    {
        _resource = resource ?? throw new ArgumentNullException(nameof(resource));
    }

    /// <summary>
    /// Gets the environment variables associated with the callback context.
    /// </summary>
    [AspireUnion(typeof(string), typeof(ReferenceExpression))]
    public Dictionary<string, object> EnvironmentVariables { get; } = environmentVariables ?? new();

    /// <summary>
    /// Gets the CancellationToken associated with the callback context.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// An optional logger to use for logging.
    /// </summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Gets the editor used to set environment variables in polyglot callbacks.
    /// </summary>
    [AspireExport(Description = "Gets the environment variable editor")]
    internal EnvironmentEditor Environment => new(EnvironmentVariables);

    /// <summary>
    /// Gets the logger facade used by polyglot callbacks.
    /// </summary>
    [AspireExport(Description = "Gets the callback logger facade")]
    internal LogFacade Log => new(() => Logger);

    /// <summary>
    /// The resource associated with this callback context.
    /// </summary>
    /// <remarks>
    /// This will be set to the resource in all cases where Aspire invokes the callback.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the EnvironmentCallbackContext was created without a specified resource.</exception>
    [AspireExport(Description = "Gets the resource associated with this callback")]
    public IResource Resource => _resource ?? throw new InvalidOperationException($"{nameof(Resource)} is not set. This callback context is not associated with a resource.");

    /// <summary>
    /// Gets the execution context associated with this invocation of the AppHost.
    /// </summary>
    [AspireExport(Description = "Gets the execution context for this callback invocation")]
    public DistributedApplicationExecutionContext ExecutionContext { get; } = executionContext ?? throw new ArgumentNullException(nameof(executionContext));
}
