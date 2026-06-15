// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes a local process that is started when a process-backed resource command executes.
/// </summary>
/// <param name="executablePath">
/// The executable path or command name to start. Command names are resolved from the AppHost process PATH.
/// </param>
[Experimental("ASPIREPROCESSCOMMAND001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ProcessCommandSpec(string executablePath)
{
    /// <summary>
    /// Gets the executable path or command name to start.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Command names without directory separators are resolved from the AppHost process PATH before starting the process.
    /// </para>
    /// </remarks>
    public string ExecutablePath { get; } = !string.IsNullOrWhiteSpace(executablePath)
        ? executablePath
        : throw new ArgumentException("The executable path cannot be null, empty, or whitespace.", nameof(executablePath));

    /// <summary>
    /// Gets or sets the working directory for the process.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the command-line arguments for the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Arguments are passed using <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> so that each item is
    /// escaped according to the current platform's process-start rules.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the environment variables to set for the process.
    /// </summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets or sets a value indicating whether the process should inherit the current environment variables.
    /// </summary>
    public bool InheritEnvironmentVariables { get; init; } = true;

    /// <summary>
    /// Gets or sets standard input content to write to the process after it starts.
    /// </summary>
    public string? StandardInputContent { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the entire process tree should be killed when the process is disposed.
    /// </summary>
    public bool KillEntireProcessTree { get; init; } = true;
}
