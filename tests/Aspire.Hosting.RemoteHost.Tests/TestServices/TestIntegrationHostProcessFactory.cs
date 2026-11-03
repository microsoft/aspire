// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost.Tests;

internal sealed class TestIntegrationHostProcessFactory : IChildProcessFactory
{
    public ProcessStartInfo? StartInfo { get; private set; }
    public ChildProcessOptions? Options { get; private set; }
    public int CreateCount { get; private set; }
    public int StartCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool StartResult { get; set; }
    public Exception? StartError { get; set; }
    public Exception? DisposeError { get; set; }

    public IChildProcess Create(ProcessStartInfo startInfo, ILogger logger, ChildProcessOptions options, bool isWindows)
    {
        CreateCount++;
        StartInfo = startInfo;
        Options = options;

        return new TestChildProcess(this, startInfo);
    }

    private sealed class TestChildProcess(TestIntegrationHostProcessFactory factory, ProcessStartInfo startInfo) : IChildProcess
    {
        public string FileName => startInfo.FileName;
        public IReadOnlyList<string> Arguments => startInfo.ArgumentList;
        public IReadOnlyDictionary<string, string?> EnvironmentVariables => new ReadOnlyDictionary<string, string?>(startInfo.Environment);
        public int ProcessId => 0;
        public DateTimeOffset? StartTime => null;
        public bool HasExited => throw new InvalidOperationException("The test child did not start.");
        public int ExitCode => throw new InvalidOperationException("The test child did not start.");

        public Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            factory.StartCount++;

            return factory.StartError is { } error ? Task.FromException<bool>(error) : Task.FromResult(factory.StartResult);
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("The test child did not start.");
        public void Kill(bool entireProcessTree) => throw new InvalidOperationException("The test child did not start.");

        public ValueTask DisposeAsync()
        {
            factory.DisposeCount++;

            return factory.DisposeError is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
        }
    }
}
