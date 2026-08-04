// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.ApplicationModel;

using IArgCallbackAnnotation = ICallbackResourceAnnotation<CommandLineArgsCallbackContext, IList<object>>;

/// <summary>
/// Carries the <em>entrypoint arguments</em> of a resource: the tool-invocation prefix that hosts the
/// program, such as <c>run -tags=netgo ./cmd/api</c> for <c>go</c> or <c>-m flask</c> for <c>python</c>.
/// </summary>
/// <remarks>
/// <para>
/// Entrypoint arguments are modelled separately from ordinary <see cref="CommandLineArgsCallbackAnnotation"/>
/// arguments for two reasons:
/// </para>
/// <list type="number">
/// <item><description>
/// They are always placed <em>first</em>, no matter when the annotation was added. The callback is evaluated
/// against its own empty argument list and the result is inserted ahead of every other argument, so no
/// <c>WithArgs</c> callback can observe it, mutate it, or clear it, and no registration order is implied.
/// </description></item>
/// <item><description>
/// When an IDE debug launch configuration owns the entrypoint (the debugger launches the built binary or the
/// interpreter itself), the prefix must not be passed to the program. Because it is a separate, structurally
/// leading list, it can simply be withheld instead of being textually subtracted from the final command line.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class EntrypointArgsCallbackAnnotation : IResourceAnnotation, IArgCallbackAnnotation
{
    private Task<IList<object>>? _callbackTask;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EntrypointArgsCallbackAnnotation"/> class.
    /// </summary>
    /// <param name="launchConfigurationType">
    /// The debug launch configuration type that owns this entrypoint, for example "go" or "python".
    /// </param>
    /// <param name="callback">
    /// Callback that produces the entrypoint arguments. It is invoked with an empty
    /// <see cref="CommandLineArgsCallbackContext.Args"/> list; everything it adds becomes the leading arguments.
    /// </param>
    public EntrypointArgsCallbackAnnotation(string launchConfigurationType, Func<CommandLineArgsCallbackContext, Task> callback)
    {
        ArgumentException.ThrowIfNullOrEmpty(launchConfigurationType);
        ArgumentNullException.ThrowIfNull(callback);

        LaunchConfigurationType = launchConfigurationType;
        Callback = callback;
    }

    /// <summary>
    /// Gets the debug launch configuration type that owns this entrypoint.
    /// </summary>
    public string LaunchConfigurationType { get; }

    /// <summary>
    /// Gets the callback that produces the entrypoint arguments.
    /// </summary>
    public Func<CommandLineArgsCallbackContext, Task> Callback { get; }

    internal IArgCallbackAnnotation AsCallbackAnnotation() => this;

    Task<IList<object>> IArgCallbackAnnotation.EvaluateOnceAsync(CommandLineArgsCallbackContext context)
    {
        lock (_lock)
        {
            _callbackTask ??= ExecuteCallbackAsync(context);
            return _callbackTask;
        }
    }

    void IArgCallbackAnnotation.ForgetCachedResult()
    {
        lock (_lock)
        {
            _callbackTask = null;
        }
    }

    private async Task<IList<object>> ExecuteCallbackAsync(CommandLineArgsCallbackContext context)
    {
        await Callback(context).ConfigureAwait(false);
        return context.Args.ToImmutableList();
    }
}

/// <summary>
/// Reports how many of the leading arguments in an execution configuration were produced by an
/// <see cref="EntrypointArgsCallbackAnnotation"/>, so that consumers that compose the actual command line
/// (such as the DCP executable creator) can tell the tool-invocation prefix apart from the program arguments.
/// </summary>
/// <param name="Count">
/// The number of leading arguments that form the entrypoint prefix. This is normalized after value resolution to
/// exclude arguments that resolve to <see langword="null"/>.
/// </param>
internal sealed record EntrypointArgumentsData(int Count) : IExecutionConfigurationData;
