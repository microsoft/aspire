// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Optional configuration for resource process commands added with <see cref="ResourceBuilderExtensions.WithProcessCommand{TResource}(IResourceBuilder{TResource}, string, string, Func{ExecuteCommandContext, ValueTask{ProcessCommandSpec}}, ProcessCommandOptions?)"/>.
/// </summary>
[Experimental("ASPIREPROCESSCOMMAND001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class ProcessCommandOptions : CommandOptions
{
    private int _maxOutputLineCount = 50;
    private IReadOnlyList<int> _successExitCodes = [0];

    internal static new ProcessCommandOptions Default => new();

    /// <summary>
    /// Gets or sets the maximum number of stdout and stderr output lines returned as command result data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Standard output and standard error are captured together in the order observed by the process runner. The returned
    /// command result contains the retained tail of the combined output as plain text.
    /// </para>
    /// <para>
    /// This option is not applied by default result handling when <see cref="GetCommandResult"/> is specified.
    /// </para>
    /// </remarks>
    public int MaxOutputLineCount
    {
        get => _maxOutputLineCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxOutputLineCount = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether returned command output should be displayed immediately in the dashboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default value is <see langword="true"/>.
    /// </para>
    /// <para>
    /// This option is not applied by default result handling when <see cref="GetCommandResult"/> is specified.
    /// </para>
    /// </remarks>
    public bool DisplayImmediately { get; set; } = true;

    /// <summary>
    /// Gets or sets the exit codes that are treated as a successful command invocation when <see cref="GetCommandResult"/> is not specified.
    /// </summary>
    /// <remarks>
    /// The default value is <c>[0]</c>.
    /// </remarks>
    public IReadOnlyList<int> SuccessExitCodes
    {
        get => _successExitCodes;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Count == 0)
            {
                throw new ArgumentException("At least one process command success exit code must be specified.", nameof(value));
            }

            _successExitCodes = value.ToArray();
        }
    }

    /// <summary>
    /// Gets or sets a callback to be invoked after the process exits to determine the result of the command invocation.
    /// </summary>
    /// <remarks>
    /// When specified, <see cref="SuccessExitCodes"/>, <see cref="MaxOutputLineCount"/>, and <see cref="DisplayImmediately"/>
    /// are not applied by the default result handling. The callback can use <see cref="ProcessCommandResultContext.GetFormattedOutput"/>
    /// to format retained process output.
    /// </remarks>
    public Func<ProcessCommandResultContext, Task<ExecuteCommandResult>>? GetCommandResult { get; set; }
}

/// <summary>
/// ATS-friendly configuration for resource process commands.
/// </summary>
[AspireDto]
internal sealed class ProcessCommandExportOptions
{
    /// <summary>
    /// The executable path or command name to start.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// The command-line arguments for the process.
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; set; }

    /// <summary>
    /// The working directory for the process.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// The environment variables to set for the process.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// A value indicating whether the process should inherit the current environment variables.
    /// </summary>
    public bool? InheritEnvironmentVariables { get; set; }

    /// <summary>
    /// Standard input content to write to the process after it starts.
    /// </summary>
    public string? StandardInputContent { get; set; }

    /// <summary>
    /// A value indicating whether the entire process tree should be killed when the process is disposed.
    /// </summary>
    public bool? KillEntireProcessTree { get; set; }

    /// <summary>
    /// A callback that creates the local process specification when the command is invoked.
    /// </summary>
    public Func<ExecuteCommandContext, Task<ProcessCommandSpecExportData>>? CreateProcessSpec { get; init; }

    /// <summary>
    /// Optional command configuration.
    /// </summary>
    public CommandOptions? CommandOptions { get; set; }

    /// <summary>
    /// The maximum number of stdout and stderr output lines returned as command result data.
    /// </summary>
    public int? MaxOutputLineCount { get; set; }

    /// <summary>
    /// A value indicating whether returned command output should be displayed immediately in the dashboard.
    /// </summary>
    public bool? DisplayImmediately { get; set; }

    /// <summary>
    /// The exit codes that are treated as a successful command invocation.
    /// </summary>
    public IReadOnlyList<int>? SuccessExitCodes { get; set; }
}

/// <summary>
/// ATS-friendly process specification for resource process command callbacks.
/// </summary>
[AspireDto]
internal sealed class ProcessCommandSpecExportData
{
    /// <summary>
    /// The executable path or command name to start.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// The command-line arguments for the process.
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; set; }

    /// <summary>
    /// The working directory for the process.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// The environment variables to set for the process.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// A value indicating whether the process should inherit the current environment variables.
    /// </summary>
    public bool? InheritEnvironmentVariables { get; set; }

    /// <summary>
    /// Standard input content to write to the process after it starts.
    /// </summary>
    public string? StandardInputContent { get; set; }

    /// <summary>
    /// A value indicating whether the entire process tree should be killed when the process is disposed.
    /// </summary>
    public bool? KillEntireProcessTree { get; set; }
}

/// <summary>
/// ATS-friendly result and command configuration for resource process commands.
/// </summary>
[AspireDto]
internal sealed class ProcessCommandResultExportOptions
{
    /// <summary>
    /// Optional command configuration.
    /// </summary>
    public CommandOptions? CommandOptions { get; set; }

    /// <summary>
    /// The maximum number of stdout and stderr output lines returned as command result data.
    /// </summary>
    public int? MaxOutputLineCount { get; set; }

    /// <summary>
    /// A value indicating whether returned command output should be displayed immediately in the dashboard.
    /// </summary>
    public bool? DisplayImmediately { get; set; }

    /// <summary>
    /// The exit codes that are treated as a successful command invocation.
    /// </summary>
    public IReadOnlyList<int>? SuccessExitCodes { get; set; }
}
