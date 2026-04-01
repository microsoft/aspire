// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.ApplicationModel;

using IArgCallbackAnnotation = ICallbackResourceAnnotation<CommandLineArgsCallbackContext, IList<object>>;

/// <summary>
/// Represents an annotation that provides a callback to be executed with a list of command-line arguments when an executable resource is started.
/// </summary>
public class CommandLineArgsCallbackAnnotation : IResourceAnnotation, IArgCallbackAnnotation
{
    private Task<IList<object>>? _callbackTask;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandLineArgsCallbackAnnotation"/> class with the specified callback action.
    /// </summary>
    /// <param name="callback"> The callback action to be executed.</param>
    public CommandLineArgsCallbackAnnotation(Func<CommandLineArgsCallbackContext, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Callback = callback;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandLineArgsCallbackAnnotation"/> class with the specified callback action.
    /// </summary>
    /// <param name="callback"> The callback action to be executed.</param>
    public CommandLineArgsCallbackAnnotation(Action<IList<object>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Callback = (c) =>
        {
            callback(c.Args);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Gets the callback action to be executed when the executable arguments are parsed.
    /// </summary>
    public Func<CommandLineArgsCallbackContext, Task> Callback { get; }

    internal IArgCallbackAnnotation AsCallbackAnnotation() => this;

    Task<IList<object>> IArgCallbackAnnotation.EvaluateOnceAsync(CommandLineArgsCallbackContext context)
    {
        lock(_lock)
        {
            if (_callbackTask is null)
            {
                _callbackTask = ExecuteCallbackAsync(context);
            }
            return _callbackTask;
        }
    }

    void IArgCallbackAnnotation.ForgetCachedResult()
    {
        lock(_lock)
        {
            _callbackTask = null;
        }
    }

    private async Task<IList<object>> ExecuteCallbackAsync(CommandLineArgsCallbackContext context)
    {
        await Callback(context).ConfigureAwait(false);
        var result = context.Args.ToImmutableList();
        return result;
    }
}

/// <summary>
/// Represents a callback context for the list of command-line arguments associated with an executable resource.
/// </summary>
/// <param name="args"> The list of command-line arguments.</param>
/// <param name="cancellationToken"> The cancellation token associated with this execution.</param>
[AspireExport(ExposeProperties = true)]
public sealed class CommandLineArgsCallbackContext(IList<object> args, CancellationToken cancellationToken = default)
{
    private readonly IResource? _resource;

    /// <summary>
    /// Represents a callback context for the list of command-line arguments associated with an executable resource.
    /// </summary>
    /// <param name="args"> The list of command-line arguments.</param>
    /// <param name="resource"> The resource associated with this callback context.</param>
    /// <param name="cancellationToken"> The cancellation token associated with this execution.</param>
    public CommandLineArgsCallbackContext(IList<object> args, IResource resource, CancellationToken cancellationToken = default)
        : this(args, cancellationToken) => _resource = resource ?? throw new ArgumentNullException(nameof(resource));

    /// <summary>
    /// Gets the list of command-line arguments.
    /// </summary>
    public IList<object> Args { get; } = args ?? throw new ArgumentNullException(nameof(args));

    /// <summary>
    /// Gets the cancellation token associated with the callback context.
    /// </summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>
    /// Gets or sets the execution context for the distributed application.
    /// </summary>
    public DistributedApplicationExecutionContext ExecutionContext { get; init; } = new(DistributedApplicationOperation.Run);

    /// <summary>
    /// Gets or sets the logger for the distributed application.
    /// </summary>
    public ILogger Logger { get; init; } = NullLogger.Instance;

    /// <summary>
    /// The resource associated with this callback context.
    /// </summary>
    /// <remarks>
    /// This will be set to the resource in all cases where Aspire invokes the callback.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the EnvironmentCallbackContext was created without a specified resource.</exception>
    public IResource Resource => _resource ?? throw new InvalidOperationException($"{nameof(Resource)} is not set. This callback context is not associated with a resource.");
}
