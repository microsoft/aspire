// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dcp.Process;

/// <summary>
/// Runs external processes.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs a process with the specified configuration.
    /// </summary>
    (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec);
}

/// <summary>
/// Runs external processes by delegating to <see cref="ProcessUtil"/>.
/// </summary>
internal sealed class DefaultProcessRunner : IProcessRunner
{
    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
        => ProcessUtil.Run(processSpec);
}
