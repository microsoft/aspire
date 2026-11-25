// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Adapts the shared SDK probe to the integration's private process implementation.
/// </summary>
internal sealed class DotnetCliToolSdkVersionProvider
{
    private readonly DotnetSdkVersionProbe _probe;

    public DotnetCliToolSdkVersionProvider(IHostApplicationLifetime lifetime, ILogger<DotnetCliToolSdkVersionProvider> logger)
        : this(StartProbe, logger, lifetime.ApplicationStopping)
    {
    }

    internal DotnetCliToolSdkVersionProvider(
        DotnetSdkProbeRunner runProbe,
        ILogger<DotnetCliToolSdkVersionProvider> logger,
        CancellationToken applicationStopping)
    {
        _probe = new(runProbe, logger, applicationStopping);
    }

    public Task<SemVersion?> TryGetVersionAsync(string workingDirectory, CancellationToken cancellationToken)
        => _probe.TryGetVersionAsync(workingDirectory, cancellationToken);

    private static (Task<DotnetSdkProbeResult>, IAsyncDisposable) StartProbe(
        string executable, string directory, IReadOnlyDictionary<string, string> environment)
    {
        var (result, process) = ProcessUtil.Run(new ProcessSpec(executable)
        {
            WorkingDirectory = directory,
            ArgumentList = ["--version"],
            EnvironmentVariables = new Dictionary<string, string>(environment),
            ResolveExecutablePath = true,
            RetainedOutputLineCount = 16,
            ThrowOnNonZeroReturnCode = false
        });
        return (ConvertResultAsync(result), process);
    }

    private static async Task<DotnetSdkProbeResult> ConvertResultAsync(Task<ProcessResult> task)
    {
        var result = await task.ConfigureAwait(false);
        return new(result.ExitCode, result.ProcessOutput);
    }
}
