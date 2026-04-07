// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Context containing all information needed to run an AppHost.
/// </summary>
internal sealed class AppHostProjectContext
{
    /// <summary>
    /// Gets the AppHost file to run.
    /// </summary>
    public required FileInfo AppHostFile { get; init; }

    /// <summary>
    /// Gets whether to run in watch mode (hot reload).
    /// </summary>
    public bool Watch { get; init; }

    /// <summary>
    /// Gets whether to run in debug mode.
    /// </summary>
    public bool Debug { get; init; }

    /// <summary>
    /// Gets whether to skip building before running.
    /// </summary>
    public bool NoBuild { get; init; }

    /// <summary>
    /// Gets whether to skip restoring packages before running.
    /// </summary>
    public bool NoRestore { get; init; }

    /// <summary>
    /// Gets whether to wait for a debugger to attach.
    /// </summary>
    public bool WaitForDebugger { get; init; }

    /// <summary>
    /// Gets whether to run in isolated mode.
    /// In isolated mode, ports are randomized and user secrets are isolated.
    /// </summary>
    public bool Isolated { get; init; }

    /// <summary>
    /// Gets whether to start a debug session (for extension hosts).
    /// </summary>
    public bool StartDebugSession { get; init; }

    /// <summary>
    /// Gets additional environment variables to pass to the AppHost.
    /// </summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets any unmatched command-line tokens to pass through to the AppHost.
    /// </summary>
    public string[] UnmatchedTokens { get; init; } = [];

    /// <summary>
    /// Gets the task completion source for the backchannel connection.
    /// The project signals this when the backchannel is ready.
    /// </summary>
    public TaskCompletionSource<IAppHostCliBackchannel>? BackchannelCompletionSource { get; init; }

    /// <summary>
    /// Gets the task completion source for build completion.
    /// The project signals this when the build/preparation phase is complete.
    /// This allows RunCommand to coordinate status spinners.
    /// </summary>
    public TaskCompletionSource<bool>? BuildCompletionSource { get; init; }

    /// <summary>
    /// Gets the parse result from the command line.
    /// </summary>
    public ParseResult? ParseResult { get; init; }

    /// <summary>
    /// Gets the working directory for the command.
    /// </summary>
    public required DirectoryInfo WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the output collector for capturing stdout/stderr.
    /// Project implementations populate this during execution.
    /// Commands can access it for error display.
    /// </summary>
    public OutputCollector? OutputCollector { get; set; }
}
