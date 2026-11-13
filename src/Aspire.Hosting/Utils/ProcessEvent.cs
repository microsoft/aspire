// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Represents output, independent output EOF, and invocation lifecycle events in one stream.
/// </summary>
/// <typeparam name="T">The output payload type. The event producer determines payload ownership and lifetime.</typeparam>
internal abstract record ProcessEvent<T>
{
    /// <summary>
    /// Indicates that launch succeeded, before any output events.
    /// </summary>
    /// <param name="ProcessId">The identifier of the launched process.</param>
    public sealed record Started(int ProcessId) : ProcessEvent<T>;

    /// <summary>
    /// Contains a standard output payload.
    /// </summary>
    /// <param name="Content">The payload supplied by the event producer.</param>
    public sealed record StandardOutput(T Content) : ProcessEvent<T>;

    /// <summary>
    /// Contains a standard error payload.
    /// </summary>
    /// <param name="Content">The payload supplied by the event producer.</param>
    public sealed record StandardError(T Content) : ProcessEvent<T>;

    /// <summary>
    /// Indicates that standard output reached EOF after its last payload.
    /// </summary>
    public sealed record StandardOutputCompleted : ProcessEvent<T>;

    /// <summary>
    /// Indicates that standard error reached EOF after its last payload.
    /// </summary>
    public sealed record StandardErrorCompleted : ProcessEvent<T>;

    /// <summary>
    /// Indicates that execution completed naturally after both output EOF events and owned resource disposal.
    /// </summary>
    /// <param name="ExitCode">The process exit code, including nonzero codes.</param>
    public sealed record Exited(int ExitCode) : ProcessEvent<T>;
}
