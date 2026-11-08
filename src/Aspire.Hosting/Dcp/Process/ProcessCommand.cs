// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Process;

/// <summary>
/// Describes process launch, input and termination policy independently of output presentation.
/// </summary>
internal sealed class ProcessCommand(string executablePath)
{
    /// <summary>The executable path or command name to launch.</summary>
    public string ExecutablePath { get; } = executablePath;
    /// <summary>Arguments passed separately, without shell parsing for native executables.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];
    /// <summary>The child working directory; null inherits the current directory.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Variables overlaid on the inherited or cleared child environment.</summary>
    public IDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
    /// <summary>Whether to inherit the parent environment before applying overrides.</summary>
    public bool InheritEnvironmentVariables { get; init; } = true;
    /// <summary>Whether to resolve command names through PATH before launch.</summary>
    public bool ResolveExecutablePath { get; init; }
    /// <summary>Whether abort terminates the root process alone or its process tree.</summary>
    public bool KillEntireProcessTree { get; init; } = true;
    /// <summary>Input delivered before EOF; null closes stdin without sending bytes.</summary>
    public ProcessInput? Input { get; init; }
}
