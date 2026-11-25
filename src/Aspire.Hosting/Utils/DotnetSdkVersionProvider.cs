// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Hosting.Utils;

internal interface IDotnetSdkVersionProvider
{
    Task<SemVersion?> TryGetVersionAsync(string? workingDirectory, CancellationToken cancellationToken);

    Task<SemVersion?> TryGetVersionAsync(
        string? workingDirectory,
        string dotnetExecutablePath,
        CancellationToken cancellationToken);

    Task<bool> SupportsMultiThreadedBuildAsync(
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken);

    Task<bool> SupportsFileBasedMultiThreadedBuildAsync(
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken);
}

internal sealed class DotnetSdkVersionProvider : IDotnetSdkVersionProvider
{
    private readonly DotnetSdkVersionProbe _probe;

    public DotnetSdkVersionProvider(
        IProcessRunner processRunner,
        IHostApplicationLifetime applicationLifetime,
        ILogger<DotnetSdkVersionProvider> logger)
        : this(processRunner, logger, applicationLifetime.ApplicationStopping)
    {
    }

    internal DotnetSdkVersionProvider(
        IProcessRunner processRunner,
        ILogger<DotnetSdkVersionProvider> logger,
        CancellationToken applicationStopping)
    {
        _probe = new(
            (executable, directory, environment) => RunProbe(processRunner, executable, directory, environment),
            logger,
            applicationStopping);
    }

    public Task<SemVersion?> TryGetVersionAsync(string? workingDirectory, CancellationToken cancellationToken)
        => _probe.TryGetVersionAsync(workingDirectory, cancellationToken);

    public Task<SemVersion?> TryGetVersionAsync(
        string? workingDirectory,
        string dotnetExecutablePath,
        CancellationToken cancellationToken)
        => _probe.TryGetVersionAsync(workingDirectory, dotnetExecutablePath, cancellationToken);

    public async Task<bool> SupportsMultiThreadedBuildAsync(
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
        => DotnetSdkUtils.SupportsMultiThreadedBuild(
            await _probe.TryGetVersionAsync(workingDirectory, "dotnet", environmentVariables, cancellationToken).ConfigureAwait(false));

    public async Task<bool> SupportsFileBasedMultiThreadedBuildAsync(
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
        => DotnetSdkUtils.SupportsFileBasedMultiThreadedBuild(
            await _probe.TryGetVersionAsync(workingDirectory, "dotnet", environmentVariables, cancellationToken).ConfigureAwait(false));

    private static (Task<DotnetSdkProbeResult>, IAsyncDisposable) RunProbe(
        IProcessRunner processRunner,
        string executable,
        string directory,
        IReadOnlyDictionary<string, string> environment)
    {
        var (result, process) = processRunner.Run(new ProcessSpec(executable)
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

    private static async Task<DotnetSdkProbeResult> ConvertResultAsync(Task<ProcessResult> resultTask)
    {
        var result = await resultTask.ConfigureAwait(false);
        return new(result.ExitCode, result.ProcessOutput);
    }
}
