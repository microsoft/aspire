// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Process;

/// <summary>
/// Owns external process execution and cleanup for each invocation.
/// </summary>
internal partial interface IProcessRunner
{
    /// <summary>
    /// Creates one pending invocation. The stream permits one enumerator acquisition, even if never advanced.
    /// The first advance launches the process; cancellation or disposal aborts and joins execution.
    /// </summary>
    IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(ProcessCommand command);
}
