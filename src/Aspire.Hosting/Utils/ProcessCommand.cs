// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Describes process launch, input, and termination policy independently of output presentation.
/// </summary>
/// <param name="executablePath">The executable path or command name to launch.</param>
internal sealed class ProcessCommand(string executablePath)
{
    /// <summary>
    /// Gets the executable path or command name to launch.
    /// </summary>
    public string ExecutablePath { get; } = executablePath;

    /// <summary>
    /// Gets the arguments passed separately, without shell parsing for native executables.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets the child working directory, or <c>null</c> to inherit the current directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets the variables overlaid on the inherited or cleared child environment.
    /// </summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets whether to inherit the parent environment before applying overrides. Defaults to <c>true</c>.
    /// </summary>
    public bool InheritEnvironmentVariables { get; init; } = true;

    /// <summary>
    /// Gets whether to resolve command names through PATH before launch. Defaults to <c>false</c>.
    /// </summary>
    public bool ResolveExecutablePath { get; init; }

    /// <summary>
    /// Gets whether abort terminates the entire process tree. Defaults to <c>true</c>.
    /// </summary>
    public bool KillEntireProcessTree { get; init; } = true;

    /// <summary>
    /// Gets the input delivered before closing standard input to signal end of input (EOF).
    /// A <c>null</c> value closes standard input without sending bytes.
    /// </summary>
    public ProcessInput? Input { get; init; }
}
