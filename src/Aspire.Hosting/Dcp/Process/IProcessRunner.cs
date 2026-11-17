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

    /// <summary>
    /// Captures UTF-8 output and error text while sending exact input bytes followed by EOF.
    /// </summary>
    Task<ProcessCaptureResult> RunAndCaptureTextAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ReadOnlyMemory<byte> standardInput = default,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs external processes by delegating to <see cref="ProcessUtil"/>.
/// </summary>
internal sealed class DefaultProcessRunner : IProcessRunner
{
    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
        => ProcessUtil.Run(processSpec);

    public Task<ProcessCaptureResult> RunAndCaptureTextAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ReadOnlyMemory<byte> standardInput = default,
        CancellationToken cancellationToken = default)
        => ProcessUtil.RunAndCaptureTextAsync(executablePath, arguments, workingDirectory, environmentVariables, standardInput, cancellationToken);
}
