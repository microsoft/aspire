// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Tests.Pipelines;

internal sealed class PipelineOutputTestProcessRunner(Func<IReadOnlyList<string>, ProcessCaptureResult> capture) : IProcessRunner
{
    internal List<IReadOnlyList<string>> Calls { get; } = [];
    internal List<byte[]> Inputs { get; } = [];

    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec) => throw new NotSupportedException();

    public Task<ProcessCaptureResult> RunAndCaptureTextAsync(string executablePath, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string>? environmentVariables = null,
        ReadOnlyMemory<byte> standardInput = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(arguments.ToArray());
        Inputs.Add(standardInput.ToArray());
        Assert.Equal("0", environmentVariables!["GIT_OPTIONAL_LOCKS"]);
        return Task.FromResult(capture(arguments));
    }

    public IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(ProcessCommand command)
        => new ProcessEventStream(ReadEventsAsync(command));

    private async IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> ReadEventsAsync(
        ProcessCommand command, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(command.Arguments.ToArray());
        Inputs.Add(command.Input?.Bytes.ToArray() ?? []);
        Assert.True(command.InheritEnvironmentVariables);
        Assert.Equal("0", command.EnvironmentVariables["GIT_OPTIONAL_LOCKS"]);
        var result = await Task.FromResult(capture(command.Arguments));
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.Started(1);
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput(Encoding.UTF8.GetBytes(result.StandardOutput));
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardError(Encoding.UTF8.GetBytes(result.StandardError));
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted();
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted();
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.Exited(result.ExitCode);
    }
}
