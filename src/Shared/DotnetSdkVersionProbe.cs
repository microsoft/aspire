// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Probes and caches SDK versions independently of the hosting package's process implementation.
/// </summary>
internal sealed class DotnetSdkVersionProbe
{
    private const string DefaultSdkContext = "<default>";
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(5);
    private static readonly IReadOnlyDictionary<string, string> s_emptyEnvironment =
        new Dictionary<string, string>();
    private static readonly Dictionary<string, string> s_dotnetCliEnvironment = new()
    {
        ["DOTNET_NOLOGO"] = "true",
        ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "true",
        ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "true",
        ["SuppressNETCoreSdkPreviewMessage"] = "true"
    };

    private readonly DotnetSdkProbeRunner _runProbe;
    private readonly ILogger _logger;
    private readonly CancellationToken _applicationStopping;
    private readonly ConcurrentDictionary<string, Lazy<Task<SemVersion?>>> _versionsBySdkContext =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public DotnetSdkVersionProbe(
        DotnetSdkProbeRunner runProbe,
        ILogger logger,
        CancellationToken applicationStopping)
    {
        _runProbe = runProbe;
        _logger = logger;
        _applicationStopping = applicationStopping;
    }

    public async Task<SemVersion?> TryGetVersionAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        return await TryGetVersionAsync(
            workingDirectory,
            "dotnet",
            s_emptyEnvironment,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SemVersion?> TryGetVersionAsync(
        string? workingDirectory,
        string dotnetExecutablePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(dotnetExecutablePath);

        return await TryGetVersionAsync(
            workingDirectory,
            dotnetExecutablePath,
            s_emptyEnvironment,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SemVersion?> TryGetVersionAsync(
        string? workingDirectory,
        string dotnetExecutablePath,
        IReadOnlyDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);

        string normalizedWorkingDirectory;
        string sdkContext;
        Dictionary<string, string> probeEnvironment;
        try
        {
            normalizedWorkingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
            probeEnvironment = CreateProbeEnvironment(environmentVariables);
            sdkContext = GetSdkContext(dotnetExecutablePath, normalizedWorkingDirectory, probeEnvironment);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine the .NET SDK selection context.");
            return null;
        }

        var versionTask = _versionsBySdkContext.GetOrAdd(
            sdkContext,
            _ => CreateVersionTask(sdkContext, dotnetExecutablePath, normalizedWorkingDirectory, probeEnvironment));

        return await versionTask.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Lazy<Task<SemVersion?>> CreateVersionTask(
        string sdkContext,
        string dotnetExecutablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string> probeEnvironment)
    {
        Lazy<Task<SemVersion?>>? versionTask = null;
        versionTask = new Lazy<Task<SemVersion?>>(
            async () =>
            {
                var version = await ProbeVersionAsync(
                    dotnetExecutablePath,
                    workingDirectory,
                    probeEnvironment).ConfigureAwait(false);
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

    private static string GetSdkContext(
        string dotnetExecutablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        var globalJsonContext = DefaultSdkContext;
        if (FindNearestGlobalJson(workingDirectory) is { } globalJsonPath)
        {
            // SDK selection depends on global.json contents, so include a cheap content fingerprint. This preserves
            // probe reuse while allowing rebuilds to observe SDK changes without restarting the AppHost.
            var hash = XxHash3.HashToUInt64(File.ReadAllBytes(globalJsonPath));
            globalJsonContext =
                $"{globalJsonPath}\0{hash.ToString("X16", CultureInfo.InvariantCulture)}";
        }

        return $"{dotnetExecutablePath}\0{globalJsonContext}\0{GetEnvironmentFingerprint(environmentVariables)}";
    }

    private static Dictionary<string, string> CreateProbeEnvironment(
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var probeEnvironment = new Dictionary<string, string>(
            environmentVariables.Count + s_dotnetCliEnvironment.Count,
            comparer);

        foreach (var (name, value) in environmentVariables)
        {
            probeEnvironment[name] = value;
        }

        foreach (var (name, value) in s_dotnetCliEnvironment)
        {
            probeEnvironment.TryAdd(name, value);
        }

        return probeEnvironment;
    }

    private static string GetEnvironmentFingerprint(
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var hash = new XxHash3();

        // Build environments can alter executable and SDK selection in ways that evolve with the .NET host.
        // Hash the complete override set instead of maintaining a brittle allowlist. The fingerprint is used
        // only as an in-memory cache key and is never logged.
        foreach (var (name, value) in environmentVariables
            .OrderBy(static variable => variable.Key, comparer)
            .ThenBy(static variable => variable.Key, StringComparer.Ordinal))
        {
            AppendHashValue(hash, name);
            AppendHashValue(hash, value);
        }

        return Convert.ToHexString(hash.GetCurrentHash());

        static void AppendHashValue(XxHash3 hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, bytes.Length);
            hash.Append(lengthBytes);
            hash.Append(bytes);
        }
    }

    private async Task<SemVersion?> ProbeVersionAsync(
        string dotnetExecutablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string> probeEnvironment)
    {
        try
        {
            var (resultTask, process) = _runProbe(dotnetExecutablePath, workingDirectory, probeEnvironment);

            await using (process)
            {
                using var timeoutSource =
                    CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
                timeoutSource.CancelAfter(s_probeTimeout);

                DotnetSdkProbeResult result;
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

    internal static string? FindNearestGlobalJson(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        var physicalWorkingDirectory = PathNormalizer.ResolveSymlinks(Path.GetFullPath(workingDirectory));
        for (var directory = new DirectoryInfo(physicalWorkingDirectory); directory is not null; directory = directory.Parent)
        {
            var globalJsonPath = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(globalJsonPath))
            {
                return PathNormalizer.ResolveToFilesystemPath(globalJsonPath);
            }
        }

        return null;
    }
}

internal delegate (Task<DotnetSdkProbeResult> Result, IAsyncDisposable Process) DotnetSdkProbeRunner(
    string executablePath,
    string workingDirectory,
    IReadOnlyDictionary<string, string> environmentVariables);

internal readonly record struct DotnetSdkProbeResult(int ExitCode, IReadOnlyList<string> ProcessOutput);
