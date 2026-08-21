// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui;

/// <summary>
/// Context passed to build-argument callbacks registered with
/// <see cref="MauiBuildArgumentsExtensions.WithMauiBuildArguments{T}(IResourceBuilder{T}, Func{MauiBuildArgumentsCallbackContext, System.Threading.Tasks.Task})"/>
/// and
/// <see cref="MauiBuildArgumentsExtensions.WithMauiLaunchArguments{T}(IResourceBuilder{T}, Func{MauiBuildArgumentsCallbackContext, System.Threading.Tasks.Task})"/>.
/// </summary>
/// <remarks>
/// Mutate the arguments in place to add, remove, or replace the arguments that will be
/// passed to <c>dotnet</c> for the <see cref="Step"/> this callback is registered for. For arguments
/// that carry secrets (for example an MSBuild property holding a signing password), add them with
/// <see cref="AddArgument(string, bool)"/> so their values are redacted from the arguments the
/// build pipeline writes to the resource logs.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class MauiBuildArgumentsCallbackContext
{
    // Tracks the exact argument strings added via AddSensitiveArgument so the build pipeline can
    // redact them before logging. Ordinal comparison because these are literal command-line tokens.
    private readonly HashSet<string> _sensitiveArguments = new(StringComparer.Ordinal);

    private readonly IList<string> _arguments;

    internal MauiBuildArgumentsCallbackContext(
        MauiBuildStep step,
        IList<string> arguments,
        IResource resource,
        CancellationToken cancellationToken)
    {
        _arguments = arguments;
        Step = step;
        Resource = resource;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the build step this callback is participating in.
    /// </summary>
    public MauiBuildStep Step { get; }

    /// <summary>
    /// Gets the MAUI platform resource the arguments apply to.
    /// </summary>
    public IResource Resource { get; }

    /// <summary>
    /// Gets a token that is cancelled if the resource start is cancelled.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Appends an argument whose value is sensitive (for example <c>-p:AndroidSigningKeyPass=…</c>).
    /// </summary>
    /// <param name="argument">The full argument to add.</param>
    /// <param name="isSensitive"></param>
    /// <remarks>
    /// The argument is passed to <c>dotnet</c> verbatim so the build and launch still work, but the MAUI
    /// build pipeline replaces its value with a placeholder in the arguments it logs to the resource
    /// output. Launch-step arguments are additionally masked by the dashboard's command-line display.
    /// </remarks>
    public void AddArgument(string argument, bool isSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(argument);

        _arguments.Add(argument);
        if (isSensitive)
        {
            _sensitiveArguments.Add(argument);
        }
    }

    /// <summary>
    /// Produces a display-safe rendering of the arguments with values added through
    /// <see cref="AddArgument(string, bool)"/> replaced by a redaction placeholder.
    /// </summary>
    internal IEnumerable<string> GetRedactedArguments()
        => _sensitiveArguments.Count == 0
            ? _arguments
            : _arguments.Select(argument => _sensitiveArguments.Contains(argument) ? "[REDACTED]" : argument);
}
