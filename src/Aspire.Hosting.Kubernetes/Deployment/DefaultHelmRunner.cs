// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Default implementation of <see cref="IHelmRunner"/> that shells out to the helm CLI.
/// </summary>
internal sealed class DefaultHelmRunner : IHelmRunner
{
    public async Task<int> RunAsync(
        string arguments,
        string? workingDirectory = null,
        Action<string>? onOutputData = null,
        Action<string>? onErrorData = null,
        CancellationToken cancellationToken = default)
    {
        var spec = new ProcessSpec("helm")
        {
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = onOutputData ?? (_ => { }),
            OnErrorData = onErrorData ?? (_ => { }),
        };

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable.ConfigureAwait(false))
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return processResult.ExitCode;
        }
    }
}
