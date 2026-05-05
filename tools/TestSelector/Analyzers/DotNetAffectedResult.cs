// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestSelector.Analyzers;

/// <summary>
/// Result from running dotnet-affected.
/// </summary>
public sealed class DotNetAffectedResult
{
    /// <summary>
    /// Whether the command succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// List of affected project paths.
    /// </summary>
    public List<string> AffectedProjects { get; init; } = [];

    /// <summary>
    /// Error message if the command failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Standard output from the command.
    /// </summary>
    public string StdOut { get; init; } = "";

    /// <summary>
    /// Standard error from the command.
    /// </summary>
    public string StdErr { get; init; } = "";

    /// <summary>
    /// Raw output from the command (for debugging).
    /// </summary>
    public string? RawOutput { get; init; }

    /// <summary>
    /// Exit code from the process.
    /// </summary>
    public int ExitCode { get; init; }
}
