// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREINTERACTION001 // Process command arguments intentionally reuse the experimental interaction input model.

/// <summary>
/// Context passed to callback to configure <see cref="ExecuteCommandResult"/> when using
/// <see cref="ResourceBuilderExtensions.WithProcessCommand{TResource}(IResourceBuilder{TResource}, string, string, Func{ExecuteCommandContext, ValueTask{ProcessCommandSpec}}, ProcessCommandOptions?)"/>.
/// </summary>
[Experimental("ASPIREPROCESSCOMMAND001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ProcessCommandResultContext
{
    /// <summary>
    /// Gets the service provider.
    /// </summary>
    [Obsolete("Use Services instead.")]
    public IServiceProvider ServiceProvider
    {
        get => Services;
        init => Services = value;
    }

    /// <summary>
    /// Gets the service provider.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Gets the name of the resource the command was configured on.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets the logger for the command invocation.
    /// </summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Gets the invocation arguments supplied by the client when the command is executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The collection contains the arguments described by the command's <see cref="CommandOptions.Arguments"/> with their
    /// submitted values populated. CLI positional arguments are mapped by declaration order. Dashboard, MCP, and other
    /// named-payload clients are mapped by <see cref="InteractionInput.Name"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// Read a declared command argument when producing a custom result:
    /// <code>
    /// GetCommandResult = context =>
    ///     Task.FromResult(CommandResults.Success("received", context.Arguments.GetString("message")!));
    /// </code>
    /// </example>
    public InteractionInputCollection Arguments { get; init; } = new([]);

    /// <summary>
    /// Gets the process command specification used for the command invocation.
    /// </summary>
    public required ProcessCommandSpec ProcessCommandSpec { get; init; }

    /// <summary>
    /// Gets the process exit code.
    /// </summary>
    public required int ExitCode { get; init; }

    /// <summary>
    /// Gets the retained stdout and stderr output lines in the order observed by the process runner.
    /// </summary>
    public required IReadOnlyList<string> Output { get; init; }

    /// <summary>
    /// Gets the total number of stdout and stderr lines observed by the process runner.
    /// </summary>
    public required int TotalOutputLineCount { get; init; }

    /// <summary>
    /// Returns the last <paramref name="maxLines"/> process output lines formatted for display.
    /// </summary>
    /// <param name="maxLines">The maximum number of lines to include.</param>
    /// <param name="outputDescription">The label used when the output is truncated.</param>
    /// <returns>The formatted output.</returns>
    public string GetFormattedOutput(int maxLines = 50, string outputDescription = "Command output")
    {
        return ProcessOutputCapture.FormatOutput(Output, TotalOutputLineCount, maxLines, outputDescription);
    }
}

#pragma warning restore ASPIREINTERACTION001
