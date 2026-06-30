// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui.Annotations;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Checks whether a .NET MAUI workload is installed.
/// </summary>
internal sealed class MauiWorkloadChecker(IProcessRunner processRunner) : IMauiPrerequisiteChecker
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Lazy<Task<ProcessResult>>> _workloadListTasks = new(StringComparer.Ordinal);

    public string Name => ".NET MAUI workload";

    public string InstallHint => "Run `dotnet workload install maui`.";

    public string DocumentationUrl => "https://learn.microsoft.com/dotnet/maui/get-started/installation";

    public bool AppliesTo(IResource resource) => resource is IMauiPlatformResource;

    public string GetCacheKey(IResource resource)
    {
        return $"{Name}:{GetWorkloadListCacheKey(resource)}:{GetPlatformWorkloadId(resource)}";
    }

    public async Task<MauiPrerequisiteCheckResult> CheckAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await GetWorkloadListAsync(resource, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return MauiPrerequisiteCheckResult.Missing($"Unable to run `dotnet workload list`: {ex.Message}");
        }

        if (result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"`dotnet workload list` exited with code {result.ExitCode}."
                : $"`dotnet workload list` exited with code {result.ExitCode}: {result.StandardError.Trim()}";
            return MauiPrerequisiteCheckResult.Missing(details);
        }

        return IsRequiredWorkloadInstalled(result.StandardOutput, resource)
            ? MauiPrerequisiteCheckResult.Available
            : MauiPrerequisiteCheckResult.Missing("No `maui` workload was found in `dotnet workload list`.");
    }

    private async Task<ProcessResult> GetWorkloadListAsync(IResource resource, CancellationToken cancellationToken)
    {
        var cacheKey = GetWorkloadListCacheKey(resource);
        var lazyResult = _workloadListTasks.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<ProcessResult>>(
                () => processRunner.RunAsync(
                    GetDotNetExecutable(),
                    ["workload", "list"],
                    GetWorkingDirectory(resource),
                    s_timeout,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            // The workload-list process is shared across matching resources, so a single
            // resource-start cancellation must not cancel the underlying probe for other resources.
            return await lazyResult.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Only share concurrent probes. Do not cache completed output because developers can
            // install workloads and retry a failed start without restarting the AppHost.
            _workloadListTasks.TryRemove(new KeyValuePair<string, Lazy<Task<ProcessResult>>>(cacheKey, lazyResult));
        }
    }

    internal static bool ParseWorkloadOutput(string output)
    {
        return ParseInstalledWorkloadIds(output).Any(static workloadId =>
            string.Equals(workloadId, "maui", StringComparison.OrdinalIgnoreCase) ||
            workloadId.StartsWith("maui-", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsRequiredWorkloadInstalled(string output, IResource resource)
    {
        var installedWorkloads = ParseInstalledWorkloadIds(output);
        if (installedWorkloads.Contains("maui"))
        {
            return true;
        }

        return GetPlatformWorkloadId(resource) is { } requiredWorkload &&
            installedWorkloads.Contains(requiredWorkload);
    }

    private static HashSet<string> ParseInstalledWorkloadIds(string output)
    {
        // `dotnet workload list` emits a table similar to:
        //   Installed Workload Id      Manifest Version       Installation Source
        //   --------------------------------------------------------------------
        //   maui                       10.0.0/10.0.100        SDK 10.0.100
        // Newer SDKs can prefix this with "Workload version: ..."; parse by first column.
        var workloadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith("Installed", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("---", StringComparison.Ordinal) ||
                trimmed.StartsWith("Workload version", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Use ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("There are no", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (columns is [var workloadId, ..])
            {
                workloadIds.Add(workloadId);
            }
        }

        return workloadIds;
    }

    private static string? GetPlatformWorkloadId(IResource resource)
    {
        return resource switch
        {
            MauiAndroidDeviceResource or MauiAndroidEmulatorResource => "maui-android",
            MauiiOSDeviceResource or MauiiOSSimulatorResource => "maui-ios",
            MauiMacCatalystPlatformResource => "maui-maccatalyst",
            MauiWindowsPlatformResource => "maui-windows",
            _ => null
        };
    }

    private static string? GetWorkingDirectory(IResource resource)
    {
        if (resource.TryGetLastAnnotation<MauiBuildInfoAnnotation>(out var buildInfo))
        {
            return buildInfo.WorkingDirectory;
        }

        return resource is IMauiPlatformResource { Parent.ProjectPath: { } projectPath }
            ? Path.GetDirectoryName(projectPath)
            : null;
    }

    private static string GetWorkloadListCacheKey(IResource resource)
    {
        return $"{GetDotNetExecutable()}:{GetWorkingDirectory(resource)}";
    }

    private static string GetDotNetExecutable()
    {
        return ResolveDotNetExecutable(Environment.GetEnvironmentVariable, File.Exists);
    }

    internal static string ResolveDotNetExecutable(Func<string, string?> getEnvironmentVariable, Func<string, bool> fileExists)
    {
        // Prefer the same dotnet host that launched the AppHost when the SDK provides it.
        // See https://learn.microsoft.com/dotnet/core/tools/dotnet-environment-variables#dotnet_host_path.
        if (getEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnetHostPath && fileExists(dotnetHostPath))
        {
            return dotnetHostPath;
        }

        // AppHosts can also be launched through a repo-local SDK. In that case the running process is
        // the built AppHost executable, not dotnet, but the host sets DOTNET_ROOT_<ARCH> to the SDK that
        // launched it. Prefer that SDK so workload checks match the user's actual AppHost toolchain.
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        foreach (var environmentVariableName in new[] { $"DOTNET_ROOT_{architecture}", "DOTNET_ROOT" })
        {
            if (getEnvironmentVariable(environmentVariableName) is not { Length: > 0 } dotnetRoot)
            {
                continue;
            }

            var candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }
}
