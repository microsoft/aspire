// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;

namespace Aspire.Hosting.ApplicationModel;

using IArgCallbackAnnotation = ICallbackResourceAnnotation<CommandLineArgsCallbackContext, IList<object>>;

/// <summary>
/// Carries the <em>launch tool arguments</em> of a resource: the tool-invocation prefix that hosts the
/// program, such as <c>run -tags=netgo ./cmd/api</c> for <c>go</c>, <c>-m flask</c> for <c>python</c>, or
/// <c>tool exec &lt;package&gt; --yes --</c> for <c>dotnet</c>.
/// </summary>
/// <remarks>
/// <para>
/// Launch tool arguments are modelled separately from ordinary <see cref="CommandLineArgsCallbackAnnotation"/>
/// arguments for two reasons:
/// </para>
/// <list type="number">
/// <item><description>
/// They are always placed <em>first</em>, no matter when the annotation was added. The callback is evaluated
/// against its own empty argument list and the result is resolved ahead of every other argument, so no
/// <c>WithArgs</c> callback can observe it, mutate it, or clear it, and no registration order is implied.
/// </description></item>
/// <item><description>
/// When an IDE debug launch configuration owns the tool invocation (the debugger launches the built binary or
/// the interpreter itself), the prefix must not be passed to the program. Because it is a separate,
/// structurally leading list, it can simply be withheld instead of being textually subtracted from the final
/// command line.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class LaunchToolArgsCallbackAnnotation : IResourceAnnotation, IArgCallbackAnnotation
{
    private Task<IList<object>>? _callbackTask;
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LaunchToolArgsCallbackAnnotation"/> class.
    /// </summary>
    /// <param name="callback">
    /// Callback that produces the launch tool arguments. It is invoked with an empty
    /// <see cref="CommandLineArgsCallbackContext.Args"/> list; everything it adds becomes the leading arguments.
    /// </param>
    /// <param name="owningLaunchConfigurationType">
    /// The debug launch configuration type that supplies this tool invocation itself, for example "go" or
    /// "python", or <see langword="null"/> when no launch configuration owns it. See
    /// <see cref="OwningLaunchConfigurationType"/>.
    /// </param>
    /// <param name="showInCommandLine">
    /// Whether these arguments are part of the command line shown for the resource in the dashboard.
    /// </param>
    public LaunchToolArgsCallbackAnnotation(Func<CommandLineArgsCallbackContext, Task> callback, string? owningLaunchConfigurationType, bool showInCommandLine)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Callback = callback;
        OwningLaunchConfigurationType = owningLaunchConfigurationType;
        ShowInCommandLine = showInCommandLine;
    }

    /// <summary>
    /// Gets the debug launch configuration type that supplies this tool invocation itself, or
    /// <see langword="null"/> when no launch configuration owns it.
    /// </summary>
    /// <remarks>
    /// When this is <see langword="null"/> the arguments are always passed to the launched program, which is the
    /// right shape for a tool invocation that is not a debugging concern at all (for example
    /// <c>dotnet tool exec</c>). A non-null value means an IDE launched through a matching launch configuration
    /// already performs this invocation, so the arguments are withheld in that case.
    /// </remarks>
    public string? OwningLaunchConfigurationType { get; }

    /// <summary>
    /// Gets a value indicating whether these arguments are part of the command line shown for the resource in
    /// the dashboard.
    /// </summary>
    /// <remarks>
    /// Set this to <see langword="false"/> for a prefix that is pure invocation plumbing the user did not write
    /// and cannot act on. Note that a prefix which is actually executed remains visible in the resource details
    /// pane regardless, because that pane reports the process's effective arguments.
    /// </remarks>
    public bool ShowInCommandLine { get; }

    /// <summary>
    /// Gets the callback that produces the launch tool arguments.
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
/// Carries unresolved launch tool arguments separately from the mutable ordinary argument list.
/// </summary>
/// <param name="Arguments">The unresolved tool-invocation prefix.</param>
/// <param name="ShowInCommandLine">Whether the prefix is shown in the dashboard command line.</param>
internal sealed record UnresolvedLaunchToolArgumentsData(ImmutableArray<object> Arguments, bool ShowInCommandLine) : IExecutionConfigurationData;

/// <summary>
/// Reports how many of the leading arguments in an execution configuration were produced by a
/// <see cref="LaunchToolArgsCallbackAnnotation"/>, so that consumers that compose the actual command line
/// (such as the DCP executable creator) can tell the tool-invocation prefix apart from the program arguments.
/// </summary>
/// <param name="Count">
/// The number of leading arguments that form the tool-invocation prefix. This is normalized after value
/// resolution to exclude arguments that resolve to <see langword="null"/>.
/// </param>
/// <param name="ShowInCommandLine">
/// Mirrors <see cref="LaunchToolArgsCallbackAnnotation.ShowInCommandLine"/>.
/// </param>
internal sealed record LaunchToolArgumentsData(int Count, bool ShowInCommandLine) : IExecutionConfigurationData;
