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

    internal static new ProcessCommandOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets the maximum number of stdout and stderr output lines returned as command result data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Standard output and standard error are captured together in the order observed by the process runner. The returned
    /// command result contains the retained tail of the combined output as plain text.
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
    /// The default value is <see langword="true"/>.
    /// </remarks>
    public bool DisplayImmediately { get; set; } = true;
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
    public IReadOnlyList<ProcessCommandEnvironmentVariable>? EnvironmentVariables { get; set; }

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
}

/// <summary>
/// Represents an environment variable to set for a resource process command.
/// </summary>
[AspireDto]
internal sealed class ProcessCommandEnvironmentVariable
{
    /// <summary>
    /// The environment variable name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The environment variable value.
    /// </summary>
    public string? Value { get; set; }
}
