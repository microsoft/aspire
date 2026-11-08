// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Process;

/// <summary>
/// Identifies output, independent output EOF and invocation lifecycle in one stream.
/// </summary>
internal abstract record ProcessEvent<T>
{
    /// <summary>Launch succeeded; precedes all output.</summary>
    public sealed record Started(int ProcessId) : ProcessEvent<T>;
    /// <summary>A retained-safe standard output payload.</summary>
    public sealed record StandardOutput(T Content) : ProcessEvent<T>;
    /// <summary>A retained-safe standard error payload.</summary>
    public sealed record StandardError(T Content) : ProcessEvent<T>;
    /// <summary>Standard output reached EOF after its last payload.</summary>
    public sealed record StandardOutputCompleted : ProcessEvent<T>;
    /// <summary>Standard error reached EOF after its last payload.</summary>
    public sealed record StandardErrorCompleted : ProcessEvent<T>;
    /// <summary>Execution completed naturally after both EOFs and owned resource disposal.</summary>
    public sealed record Exited(int ExitCode) : ProcessEvent<T>;
}
