// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Hosting.Utils;

internal interface IDotnetSdkVersionProvider
{
    Task<SemVersion?> TryGetVersionAsync(string? workingDirectory, CancellationToken cancellationToken);

    Task<bool> SupportsMultiThreadedBuildAsync(string? workingDirectory, CancellationToken cancellationToken);
}

internal sealed class DotnetSdkVersionProvider : IDotnetSdkVersionProvider
{
    private const string DefaultSdkContext = "<default>";
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(5);
    private static readonly Dictionary<string, string> s_dotnetCliEnvironment = new()
    {
        ["DOTNET_NOLOGO"] = "true",
        ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true",
        ["SuppressNETCoreSdkPreviewMessage"] = "true"
    };

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<DotnetSdkVersionProvider> _logger;
    private readonly CancellationToken _applicationStopping;
    private readonly ConcurrentDictionary<string, Lazy<Task<SemVersion?>>> _versionsBySdkContext =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
        _processRunner = processRunner;
        _logger = logger;
        _applicationStopping = applicationStopping;
    }

    public async Task<SemVersion?> TryGetVersionAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        string normalizedWorkingDirectory;
        string sdkContext;
        try
        {
            normalizedWorkingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
            sdkContext = GetSdkContext(normalizedWorkingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine the .NET SDK selection context.");
            return null;
        }

        var versionTask = _versionsBySdkContext.GetOrAdd(
            sdkContext,
            _ => CreateVersionTask(sdkContext, normalizedWorkingDirectory));

        return await versionTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SupportsMultiThreadedBuildAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var version = await TryGetVersionAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        return DotnetSdkUtils.SupportsMultiThreadedBuild(version);
    }

    private Lazy<Task<SemVersion?>> CreateVersionTask(string sdkContext, string workingDirectory)
    {
        Lazy<Task<SemVersion?>>? versionTask = null;
        versionTask = new Lazy<Task<SemVersion?>>(
            async () =>
            {
                var version = await ProbeVersionAsync(workingDirectory).ConfigureAwait(false);
                if (version is null)
                {
                    // A later build may run after the SDK installation or global.json issue has been corrected.
                    _versionsBySdkContext.TryRemove(KeyValuePair.Create(sdkContext, versionTask!));
                }

                return version;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
        return versionTask;
    }

    private static string GetSdkContext(string workingDirectory)
    {
        if (DotnetSdkUtils.FindNearestGlobalJson(workingDirectory) is not { } globalJsonPath)
        {
            return DefaultSdkContext;
        }

        // SDK selection depends on global.json contents, so include a cheap content fingerprint. This preserves
        // probe reuse while allowing rebuilds to observe SDK changes without restarting the AppHost.
        var hash = XxHash3.HashToUInt64(File.ReadAllBytes(globalJsonPath));
        return $"{globalJsonPath}\0{hash.ToString("X16", CultureInfo.InvariantCulture)}";
    }

    private async Task<SemVersion?> ProbeVersionAsync(string workingDirectory)
    {
        try
        {
            var (resultTask, process) = _processRunner.Run(new ProcessSpec("dotnet")
            {
                WorkingDirectory = workingDirectory,
                ArgumentList = ["--version"],
                EnvironmentVariables = s_dotnetCliEnvironment,
                ResolveExecutablePath = true,
                RetainedOutputLineCount = 16,
                ThrowOnNonZeroReturnCode = false,
            });

            await using (process)
            {
                using var timeoutSource =
                    CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
                timeoutSource.CancelAfter(s_probeTimeout);

                ProcessResult result;
                try
                {
                    result = await resultTask.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_applicationStopping.IsCancellationRequested)
                {
                    _logger.LogDebug("The .NET SDK version probe was canceled because the AppHost is stopping.");
                    return null;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug(
                        "The .NET SDK version probe in '{WorkingDirectory}' timed out after {TimeoutSeconds} seconds.",
                        workingDirectory,
                        s_probeTimeout.TotalSeconds);
                    return null;
                }

                if (result.ExitCode != 0)
                {
                    _logger.LogDebug(
                        "The .NET SDK version probe in '{WorkingDirectory}' exited with code {ExitCode}.",
                        workingDirectory,
                        result.ExitCode);
                    return null;
                }

                foreach (var line in result.ProcessOutput)
                {
                    if (SemVersion.TryParse(line.Trim(), SemVersionStyles.Strict, out var version))
                    {
                        return version;
                    }
                }
            }

            _logger.LogDebug(
                "The .NET SDK version probe in '{WorkingDirectory}' did not return a valid semantic version.",
                workingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to run the .NET SDK version probe in '{WorkingDirectory}'.",
                workingDirectory);
        }

        return null;
    }
}
