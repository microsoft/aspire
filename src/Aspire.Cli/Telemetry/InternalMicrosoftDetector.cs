// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Cli.DotNet;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Detects whether the current user or machine appears to be Microsoft internal.
/// </summary>
internal interface IInternalMicrosoftDetector
{
    /// <summary>
    /// Gets whether the current user or machine appears to be Microsoft internal.
    /// </summary>
    Task<InternalMicrosoftDetectionResult> IsInternalMicrosoftMachineAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Caches and runs staged Microsoft-internal probes.
/// </summary>
internal sealed partial class InternalMicrosoftDetector : IInternalMicrosoftDetector
{
    private const string MicrosoftGitHubOrg = "microsoft";
    private const string MicrosoftTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    private const string CorpMicrosoftDomainSuffix = ".corp.microsoft.com";
    private const string CacheSubdirectoryName = "internal-microsoft";
    private const string CacheFileName = "detector.json";
    private const string VisualStudioMicrosoftTenantProbeName = "Visual Studio Microsoft tenant";
    private const string WslVisualStudioMicrosoftTenantProbeName = "WSL Visual Studio Microsoft tenant";
    private const int CacheVersion = 6;
    private const int MaxGitHubTokenCandidates = 5;

    private static readonly TimeSpan s_cacheRefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan s_probeStageTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_processProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_cancelledProbeDrainTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_gitHubHttpTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_gitHubCandidateTimeout = TimeSpan.FromSeconds(5);

    private readonly string _cacheFilePath;
    private readonly CliExecutionContext _executionContext;
    private readonly IEnvironment _environment;
    private readonly IProcessExecutionFactory _processExecutionFactory;
    private readonly HttpMessageHandler? _gitHubHttpMessageHandler;
    private readonly TimeSpan _gitHubCandidateTimeout;
    private readonly TimeSpan _gitHubHttpTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InternalMicrosoftDetector> _logger;
    private readonly ICIEnvironmentDetector _ciEnvironmentDetector;
    private readonly IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>>? _probeStages;
    private readonly TimeSpan _probeStageTimeout;

    public InternalMicrosoftDetector(CliExecutionContext executionContext, IEnvironment environment, TimeProvider timeProvider, ILogger<InternalMicrosoftDetector> logger, IProcessExecutionFactory processExecutionFactory, ICIEnvironmentDetector ciEnvironmentDetector)
        : this(
            executionContext,
            environment,
            Path.Combine(executionContext.CacheDirectory.FullName, CacheSubdirectoryName, CacheFileName),
            timeProvider,
            logger,
            processExecutionFactory,
            ciEnvironmentDetector,
            probeStages: null)
    {
    }

    internal InternalMicrosoftDetector(
        CliExecutionContext executionContext,
        IEnvironment environment,
        string cacheFilePath,
        TimeProvider timeProvider,
        ILogger<InternalMicrosoftDetector> logger,
        IProcessExecutionFactory processExecutionFactory,
        ICIEnvironmentDetector ciEnvironmentDetector,
        IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>>? probeStages,
        HttpMessageHandler? gitHubHttpMessageHandler = null,
        TimeSpan? gitHubCandidateTimeout = null,
        TimeSpan? gitHubHttpTimeout = null,
        TimeSpan? probeStageTimeout = null)
    {
        _cacheFilePath = cacheFilePath;
        _executionContext = executionContext;
        _environment = environment;
        _processExecutionFactory = processExecutionFactory;
        _gitHubHttpMessageHandler = gitHubHttpMessageHandler;
        _gitHubCandidateTimeout = gitHubCandidateTimeout ?? s_gitHubCandidateTimeout;
        _gitHubHttpTimeout = gitHubHttpTimeout ?? s_gitHubHttpTimeout;
        _timeProvider = timeProvider;
        _logger = logger;
        _ciEnvironmentDetector = ciEnvironmentDetector;
        _probeStageTimeout = probeStageTimeout ?? s_probeStageTimeout;
        _probeStages = probeStages;
    }

    public async Task<InternalMicrosoftDetectionResult> IsInternalMicrosoftMachineAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var cached = await TryReadCacheAsync(cancellationToken).ConfigureAwait(false);
            if (cached.Entry is not null && cached.CacheStatus == InternalMicrosoftDetectorCacheStatus.Hit)
            {
                stopwatch.Stop();
                return new InternalMicrosoftDetectionResult(
                    cached.Entry.IsInternalMicrosoft,
                    cached.Entry.Source,
                    cached.Entry.Alias,
                    cached.Entry.Domain,
                    cached.Entry.IsInternalMicrosoft ? InternalMicrosoftDetectorOutcome.Detected : InternalMicrosoftDetectorOutcome.NotDetected,
                    InternalMicrosoftDetectorCacheStatus.Hit,
                    stopwatch.Elapsed,
                    []);
            }

            var result = await RunProbeStagesAsync(cached.CacheStatus, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is InternalMicrosoftDetectorOutcome.Detected or InternalMicrosoftDetectorOutcome.NotDetected)
            {
                await TryWriteCacheAsync(result, cancellationToken).ConfigureAwait(false);
            }

            stopwatch.Stop();
            return result with { Duration = stopwatch.Elapsed };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Internal Microsoft detection failed.");
            }

            return new InternalMicrosoftDetectionResult(
                IsInternalMicrosoft: false,
                Source: null,
                Alias: null,
                Domain: null,
                Outcome: InternalMicrosoftDetectorOutcome.Failed,
                CacheStatus: InternalMicrosoftDetectorCacheStatus.Miss,
                Duration: stopwatch.Elapsed,
                ProbeDiagnostics: []);
        }
    }

    private IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> CreateDefaultProbeStages()
    {
        // Probes are ordered by cost and signal quality. Local account stores and OS enrollment
        // state come from standard developer-machine tooling: Windows dsregcmd, Visual Studio
        // IdentityService, macOS Platform SSO, gh/Copilot CLI auth, and
        // GitHub's organization membership API.
        // See:
        // - https://learn.microsoft.com/entra/identity/devices/troubleshoot-device-dsregcmd
        // - https://learn.microsoft.com/entra/identity/devices/macos-platform-single-sign-on
        // - https://docs.github.com/rest/orgs/members

        // Fastest/strongest signal probes
        var stage1 = new List<InternalMicrosoftProbe>();
        if (_environment.IsMacOS())
        {
            // Use the platform SSO service on MacOS as the strongest signal (indicates machine is enrolled in
            // Microsoft Intune and user has a Microsoft account in the Microsoft tenant configured in their keychain)
            stage1.Add(new("Mac Platform SSO", CheckMacPlatformSsoAsync));
        }

        // Probes that may involve more extensive process execution/network calls or are a weaker signal, run last
        // to avoid delaying detection when faster/better quality signals are available. CI environments can expose
        // GitHub tokens that identify automation rather than a human, so skip token-membership probes there.
        var stage2 = new List<InternalMicrosoftProbe>();
        if (!IsCIEnvironment())
        {
            // Is there a GitHub token in the environment that has an active membership in the Microsoft GitHub org?
            stage2.Add(new("Environment GitHub token membership", CheckEnvironmentGitHubTokenAsync));

            // Is there a GitHub token from the gh CLI that has an active membership in the Microsoft GitHub org?
            stage2.Add(new("gh CLI GitHub org membership", CheckGhCliAsync));

            // Is there a GitHub token from the Copilot CLI that has an active membership in the Microsoft GitHub org?
            stage2.Add(new("Copilot CLI GitHub org membership", CheckCopilotCliAsync));
        }

        if (_environment.IsWindows())
        {
            // Stage 1

            // Check USERDNSDOMAIN for a corp.microsoft.com domain, which is a strong signal of being on a Microsoft corporate machine or VPN.
            // This is much faster than checking workplace join status and doesn't require admin privileges, so we check it in stage 1.
            stage1.Add(new("Windows USERDNSDOMAIN", CheckWindowsUserDnsDomainAsync));

            // Check for a Microsoft tenant in the Visual Studio account store, which is a strong signal of being a Microsoft employee.
            // This is also relatively fast and doesn't require admin privileges, so we check it in stage 1.
            stage1.Add(new(VisualStudioMicrosoftTenantProbeName, CheckVisualStudioMicrosoftTenantAsync));

            // Stage 2

            // Check if the machine is workplace joined to the Microsoft tenant, which is a strong signal of being on a Microsoft corporate machine,
            // but can be slower to evaluate so we check it in stage 2.
            stage2.Add(new("Windows workplace join", CheckWindowsWorkplaceJoinAsync));
        }
        else if (IsWsl())
        {
            // Stage 1

            // Check USERDNSDOMAIN for a corp.microsoft.com domain on the Windows host, which is a strong signal of being on a Microsoft corporate machine or VPN.
            // This is much faster than checking workplace join status and doesn't require admin privileges, so we check it in stage 1.
            stage1.Add(new("WSL Windows USERDNSDOMAIN", CheckWslWindowsUserDnsDomainAsync));

            // Check for a Microsoft tenant in the Visual Studio account store on the Windows host, which is a strong signal of being a Microsoft employee.
            // This is also relatively fast and doesn't require admin privileges, so we check it in stage 1.
            stage1.Add(new(WslVisualStudioMicrosoftTenantProbeName, CheckWslVisualStudioMicrosoftTenantAsync));

            // Stage 2

            // Check if the Windows host machine is workplace joined to the Microsoft tenant, which is a strong signal of being on a Microsoft corporate machine,
            // but can be slower to evaluate so we check it in stage 2.
            stage2.Add(new("WSL Windows workplace join", CheckWslWindowsWorkplaceJoinAsync));
            stage2.Add(new("WSL Windows gh.exe GitHub org membership", CheckWslWindowsGhCliAsync));
        }

        return [stage1, stage2];
    }

    private async Task<InternalMicrosoftDetectionResult> RunProbeStagesAsync(
        string cacheStatus,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<InternalMicrosoftProbeDiagnostic>();
        var timedOut = false;
        var probeStages = _probeStages ?? CreateDefaultProbeStages();
        foreach (var stage in probeStages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stage.Count == 0)
            {
                continue;
            }

            var stageResult = await RunProbeStageAsync(stage, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(stageResult.Diagnostics);
            timedOut |= stageResult.TimedOut;
            if (stageResult.Result is not null)
            {
                return stageResult.Result with
                {
                    Outcome = InternalMicrosoftDetectorOutcome.Detected,
                    CacheStatus = cacheStatus,
                    Duration = TimeSpan.Zero,
                    ProbeDiagnostics = diagnostics
                };
            }
        }

        var anyProbeFailed = diagnostics.Any(diagnostic => diagnostic.Outcome == InternalMicrosoftProbeOutcome.Failed);
        return new InternalMicrosoftDetectionResult(
            IsInternalMicrosoft: false,
            Source: null,
            Alias: null,
            Domain: null,
            Outcome: timedOut
                ? InternalMicrosoftDetectorOutcome.TimedOut
                : anyProbeFailed
                    ? InternalMicrosoftDetectorOutcome.Failed
                    : InternalMicrosoftDetectorOutcome.NotDetected,
            CacheStatus: cacheStatus,
            Duration: TimeSpan.Zero,
            ProbeDiagnostics: diagnostics);
    }

    private async Task<InternalMicrosoftProbeStageResult> RunProbeStageAsync(IReadOnlyList<InternalMicrosoftProbe> probes, CancellationToken cancellationToken)
    {
        var stageStartTimestamp = Stopwatch.GetTimestamp();
        var stageDeadlineTimestamp = stageStartTimestamp + (long)(_probeStageTimeout.TotalSeconds * Stopwatch.Frequency);
        var stageTimeoutTimestamp = long.MaxValue;
        using var stageTimeout = new CancellationTokenSource();
        using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stageTimeout.Token);
        using var stageTimeoutRegistration = stageTimeout.Token.Register(
            () => Interlocked.Exchange(ref stageTimeoutTimestamp, Stopwatch.GetTimestamp()));
        stageTimeout.CancelAfter(_probeStageTimeout);
        var probeTasks = probes.Select(probe => RunProbeAsync(probe, stageCancellation.Token)).ToList();
        var timedOut = false;

        try
        {
            await Task.WhenAll(probeTasks).WaitAsync(stageCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && stageTimeout.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            await stageCancellation.CancelAsync().ConfigureAwait(false);
            await DrainCancelledProbesAsync(probeTasks).ConfigureAwait(false);
        }

        // Timers can fire slightly before their nominal deadline on some platforms. Once the
        // timeout token is signaled, results completed during cancellation draining are late even
        // when their timestamp precedes the originally calculated deadline.
        stageDeadlineTimestamp = Math.Min(stageDeadlineTimestamp, Volatile.Read(ref stageTimeoutTimestamp));
        var completedResults = new List<InternalMicrosoftProbeRunResult>();
        var diagnostics = new List<InternalMicrosoftProbeDiagnostic>();
        foreach (var task in probeTasks)
        {
            if (task.IsCompletedSuccessfully)
            {
                completedResults.Add(task.Result);
                var diagnostic = task.Result.Diagnostic;
                var deadlineTriggeredCancellation = timedOut && diagnostic.Outcome == InternalMicrosoftProbeOutcome.Cancelled;
                diagnostics.Add(deadlineTriggeredCancellation || task.Result.CompletionTimestamp > stageDeadlineTimestamp
                    ? diagnostic with { Outcome = InternalMicrosoftProbeOutcome.TimedOut }
                    : diagnostic);
            }
        }

        timedOut |= completedResults.Any(result => result.CompletionTimestamp > stageDeadlineTimestamp);
        foreach (var probe in probes)
        {
            if (!completedResults.Any(result => result.Source.Equals(probe.Name, StringComparison.Ordinal)))
            {
                diagnostics.Add(new InternalMicrosoftProbeDiagnostic(probe.Name, InternalMicrosoftProbeOutcome.TimedOut, _probeStageTimeout, HasAlias: false, HasDomain: false));
            }
        }

        // Probe tasks can ignore cancellation and complete while the cancellation drain runs.
        // Compare their recorded completion time with the actual stage deadline so scheduler
        // latency between deadline expiry and the timeout continuation cannot admit late results.
        var result = completedResults
            .Where(result => result.CompletionTimestamp <= stageDeadlineTimestamp)
            .Where(result => result.Result.IsInternalMicrosoft)
            .OrderByDescending(result => GetProbeResultScore(result.Result))
            .ThenBy(result => GetProbeIndex(probes, result.Probe))
            .FirstOrDefault();

        return new InternalMicrosoftProbeStageResult(
            result is { Result.IsInternalMicrosoft: true }
                ? new InternalMicrosoftDetectionResult(IsInternalMicrosoft: true, Source: result.Source, Alias: result.Result.Alias, Domain: result.Result.Domain, Outcome: InternalMicrosoftDetectorOutcome.Detected, CacheStatus: InternalMicrosoftDetectorCacheStatus.Miss, Duration: TimeSpan.Zero, ProbeDiagnostics: [])
                : null,
            diagnostics,
            timedOut);
    }

    private Task<InternalMicrosoftProbeRunResult> RunProbeAsync(InternalMicrosoftProbe probe, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await probe.DetectAsync(cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                var outcome = result.Failure is not null
                    ? InternalMicrosoftProbeOutcome.Failed
                    : result.IsInternalMicrosoft
                        ? InternalMicrosoftProbeOutcome.Detected
                        : InternalMicrosoftProbeOutcome.NotDetected;
                return new InternalMicrosoftProbeRunResult(
                    probe,
                    probe.Name,
                    result,
                    new InternalMicrosoftProbeDiagnostic(probe.Name, outcome, probe.DurationOverride ?? stopwatch.Elapsed, !string.IsNullOrEmpty(result.Alias), !string.IsNullOrEmpty(result.Domain), result.Failure),
                    Stopwatch.GetTimestamp());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new InternalMicrosoftProbeRunResult(
                    probe,
                    probe.Name,
                    InternalMicrosoftProbeResult.NotDetected,
                    new InternalMicrosoftProbeDiagnostic(probe.Name, InternalMicrosoftProbeOutcome.Cancelled, stopwatch.Elapsed, HasAlias: false, HasDomain: false),
                    Stopwatch.GetTimestamp());
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Microsoft internal probe '{ProbeName}' failed.", probe.Name);
                }
                stopwatch.Stop();
                return new InternalMicrosoftProbeRunResult(
                    probe,
                    probe.Name,
                    InternalMicrosoftProbeResult.NotDetected,
                    new InternalMicrosoftProbeDiagnostic(
                        probe.Name,
                        InternalMicrosoftProbeOutcome.Failed,
                        stopwatch.Elapsed,
                        HasAlias: false,
                        HasDomain: false,
                        Failure: CreateExceptionFailure(ex, InternalMicrosoftProbeFailureStage.Probe)),
                    Stopwatch.GetTimestamp());
            }
        }, CancellationToken.None);
    }

    private static InternalMicrosoftProbeFailure CreateExceptionFailure(Exception exception, string stage)
    {
        return new(InternalMicrosoftProbeFailureCode.Exception, stage, ExceptionType: GetSafeExceptionType(exception));
    }

    private static string GetSafeExceptionType(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => InternalMicrosoftProbeExceptionType.UnauthorizedAccess,
            IOException => InternalMicrosoftProbeExceptionType.Io,
            JsonException => InternalMicrosoftProbeExceptionType.Json,
            HttpRequestException => InternalMicrosoftProbeExceptionType.HttpRequest,
            TaskCanceledException => InternalMicrosoftProbeExceptionType.TaskCanceled,
            InvalidOperationException => InternalMicrosoftProbeExceptionType.InvalidOperation,
            _ => InternalMicrosoftProbeExceptionType.Other
        };
    }

    private static int GetProbeResultScore(InternalMicrosoftProbeResult result)
    {
        var score = 0;
        if (!string.IsNullOrEmpty(result.Alias))
        {
            score += 2;
        }

        if (!string.IsNullOrEmpty(result.Domain))
        {
            score += 1;
        }

        return score;
    }

    private static int GetProbeIndex(IReadOnlyList<InternalMicrosoftProbe> probes, InternalMicrosoftProbe probe)
    {
        for (var index = 0; index < probes.Count; index++)
        {
            if (ReferenceEquals(probes[index], probe))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private async Task DrainCancelledProbesAsync(IReadOnlyList<Task<InternalMicrosoftProbeRunResult>> probeTasks)
    {
        try
        {
            await Task.WhenAll(probeTasks).WaitAsync(s_cancelledProbeDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Timed out waiting for cancelled Microsoft internal probes to drain.");
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "A cancelled Microsoft internal probe failed while draining.");
            }
        }
    }

    private async Task<InternalMicrosoftCacheReadResult> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cacheFilePath))
        {
            return new InternalMicrosoftCacheReadResult(null, InternalMicrosoftDetectorCacheStatus.Miss);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InternalMicrosoftDetectorCacheEntry);
            if (entry is null)
            {
                return new InternalMicrosoftCacheReadResult(null, InternalMicrosoftDetectorCacheStatus.Stale);
            }

            var isCIEnvironment = IsCIEnvironment();
            if (entry.Version == 0)
            {
                // Legacy entries do not record whether the probe set ran in CI mode. Treat every
                // legacy result as stale so CI automation identity can never be reused as local
                // identity and the current probe/privacy rules always run before values are emitted.
                return new InternalMicrosoftCacheReadResult(null, InternalMicrosoftDetectorCacheStatus.Stale);
            }
            else if (entry.Version != CacheVersion || entry.IsCIEnvironment != isCIEnvironment)
            {
                return new InternalMicrosoftCacheReadResult(null, InternalMicrosoftDetectorCacheStatus.Stale);
            }

            entry = NormalizeCacheEntry(entry);
            var hasRequiredSource = !entry.IsInternalMicrosoft || !string.IsNullOrEmpty(entry.Source);
            var isFresh = hasRequiredSource && _timeProvider.GetUtcNow() - entry.LastRunUtc < s_cacheRefreshInterval;
            return isFresh
                ? new InternalMicrosoftCacheReadResult(entry, InternalMicrosoftDetectorCacheStatus.Hit)
                : new InternalMicrosoftCacheReadResult(null, InternalMicrosoftDetectorCacheStatus.Stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read Microsoft internal detector cache from {CacheFilePath}.", _cacheFilePath);
            }
            return new InternalMicrosoftCacheReadResult(null, InternalMicrosoftDetectorCacheStatus.Stale);
        }
    }

    private static InternalMicrosoftDetectorCacheEntry NormalizeCacheEntry(InternalMicrosoftDetectorCacheEntry entry)
    {
        var alias = NormalizeAlias(entry.Alias);
        var domain = NormalizeAdDomainName(entry.Domain);

        return entry with
        {
            Alias = alias,
            Domain = domain
        };
    }

    private async Task TryWriteCacheAsync(InternalMicrosoftDetectionResult result, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cacheFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        var tempPath = Path.Combine(directory, $"{Path.GetRandomFileName()}.tmp");
        try
        {
            Directory.CreateDirectory(directory);

            var entry = new InternalMicrosoftDetectorCacheEntry
            {
                Version = CacheVersion,
                IsInternalMicrosoft = result.IsInternalMicrosoft,
                Source = result.Source,
                Alias = result.Alias,
                Domain = result.Domain,
                IsCIEnvironment = IsCIEnvironment(),
                LastRunUtc = _timeProvider.GetUtcNow()
            };
            var json = JsonSerializer.Serialize(entry, JsonSourceGenerationContext.Default.InternalMicrosoftDetectorCacheEntry);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _cacheFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to write Microsoft internal detector cache to {CacheFilePath}.", _cacheFilePath);
            }
        }
        finally
        {
            FileDeleteHelper.TryDeleteFile(tempPath);
        }
    }

    internal Task<InternalMicrosoftProbeResult> CheckWindowsUserDnsDomainAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userDnsDomain = _environment.GetEnvironmentVariable("USERDNSDOMAIN");
        var domain = ExtractAdDomainNameFromCorpDnsName(userDnsDomain);
        return Task.FromResult(domain is not null
            ? Detected(_environment.GetEnvironmentVariable("USERNAME"), domain)
            : InternalMicrosoftProbeResult.NotDetected);
    }

    private async Task<InternalMicrosoftProbeResult> CheckWslWindowsUserDnsDomainAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("cmd.exe", ["/c", "echo %USERDNSDOMAIN%&echo %USERNAME%"], cancellationToken).ConfigureAwait(false);
        if (GetProcessFailure(result, treatNonZeroExitAsFailure: true) is { } processFailure)
        {
            return processFailure;
        }

        var outputLines = result.Stdout.Split('\n', StringSplitOptions.TrimEntries);
        var userDnsDomain = outputLines.FirstOrDefault() ?? string.Empty;
        var userName = outputLines.Skip(1).FirstOrDefault() ?? string.Empty;
        var domain = ExtractAdDomainNameFromCorpDnsName(userDnsDomain);
        return result.ExitCode == 0 && domain is not null
            ? Detected(userName, domain)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    [SupportedOSPlatform("windows")]
    private async Task<InternalMicrosoftProbeResult> CheckVisualStudioMicrosoftTenantAsync(CancellationToken cancellationToken)
    {
        var localAppData = GetSpecialFolderPath(Environment.SpecialFolder.LocalApplicationData, "LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var accountStore = Path.Combine(localAppData, ".IdentityService", "V3AccountStore.json");
        if (!File.Exists(accountStore))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        try
        {
            var text = await File.ReadAllTextAsync(accountStore, cancellationToken).ConfigureAwait(false);
            return DetectVisualStudioMicrosoftTenant(text, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return InternalMicrosoftProbeResult.Failed(new(
                InternalMicrosoftProbeFailureCode.FileUnreadable,
                InternalMicrosoftProbeFailureStage.AccountStore,
                ExceptionType: GetSafeExceptionType(ex)));
        }
    }

    private async Task<InternalMicrosoftProbeResult> CheckWslVisualStudioMicrosoftTenantAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync(
            "cmd.exe",
            ["/c", "if exist \"%LOCALAPPDATA%\\.IdentityService\\V3AccountStore.json\" type \"%LOCALAPPDATA%\\.IdentityService\\V3AccountStore.json\""],
            cancellationToken).ConfigureAwait(false);
        if (GetProcessFailure(result, treatNonZeroExitAsFailure: true) is { } processFailure)
        {
            return processFailure;
        }

        return DetectVisualStudioMicrosoftTenant(result.Stdout, cancellationToken);
    }

    [SupportedOSPlatform("macos")]
    private async Task<InternalMicrosoftProbeResult> CheckMacPlatformSsoAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("app-sso"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("app-sso", ["platform", "-s"], cancellationToken).ConfigureAwait(false);
        if (GetProcessFailure(result, treatNonZeroExitAsFailure: true) is { } processFailure)
        {
            return processFailure;
        }

        // app-sso emits a JSON document similar to:
        //   {"realm":"REDMOND.CORP.MICROSOFT.COM","upn":"alias@REDMOND.CORP.MICROSOFT.COM",
        //    "issuer":"https://login.microsoftonline.com/<tenant>/v2.0", ...}
        // Use JSON APIs for the fixed fields so formatting changes don't affect detection.
        var json = TryParseJsonObject($"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        if (json is null)
        {
            return InternalMicrosoftProbeResult.Failed(new(
                InternalMicrosoftProbeFailureCode.JsonParse,
                InternalMicrosoftProbeFailureStage.PlatformSso,
                ExceptionType: InternalMicrosoftProbeExceptionType.Json));
        }

        var expectedIssuer = $"https://login.microsoftonline.com/{MicrosoftTenantId}/v2.0";
        var expectedKeyEndpoint = $"https://login.microsoftonline.com/{MicrosoftTenantId}/getkeydata";
        var expectedTokenEndpoint = $"https://login.microsoftonline.com/{MicrosoftTenantId}/oauth2/v2.0/token";
        var upn = TryGetString(json, "upn");
        var realmDomain = ExtractAdDomainNameFromCorpDnsName(TryGetString(json, "realm"));
        var upnDomain = ExtractAdDomainNameFromAccountIdentifier(upn);
        var domain = realmDomain ?? upnDomain;

        return HasJsonStringProperty(json, "issuer", expectedIssuer) &&
            HasJsonStringProperty(json, "keyEndpointURL", expectedKeyEndpoint) &&
            HasJsonStringProperty(json, "tokenEndpointURL", expectedTokenEndpoint) &&
            domain is not null &&
            ExtractAliasFromAccountIdentifier(upn) is { } alias
                ? Detected(alias, domain)
                : InternalMicrosoftProbeResult.NotDetected;
    }

    internal async Task<InternalMicrosoftProbeResult> CheckWindowsWorkplaceJoinAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("dsregcmd"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("dsregcmd", ["/status"], cancellationToken).ConfigureAwait(false);
        if (GetProcessFailure(result, treatNonZeroExitAsFailure: true) is { } processFailure)
        {
            return processFailure;
        }

        return EvaluateWindowsWorkplaceJoin(
            result.Stdout,
            _environment.GetEnvironmentVariable("USERDNSDOMAIN"));
    }

    private async Task<InternalMicrosoftProbeResult> CheckWslWindowsWorkplaceJoinAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("cmd.exe", ["/c", "dsregcmd /status"], cancellationToken).ConfigureAwait(false);
        if (GetProcessFailure(result, treatNonZeroExitAsFailure: true) is { } processFailure)
        {
            return processFailure;
        }

        return EvaluateWindowsWorkplaceJoin(result.Stdout, fallbackDomain: null);
    }

    private async Task<InternalMicrosoftProbeResult> CheckGhCliAsync(CancellationToken cancellationToken)
    {
        if (IsCIEnvironment())
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        if (!CommandExists("gh"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var tokenResult = await RunProcessAsync("gh", ["auth", "token", "--hostname", "github.com"], cancellationToken).ConfigureAwait(false);
        if (GetProcessFailure(tokenResult, treatNonZeroExitAsFailure: false) is { } processFailure)
        {
            return processFailure;
        }

        if (tokenResult.ExitCode != 0 || string.IsNullOrWhiteSpace(tokenResult.Stdout))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        using var http = CreateGitHubHttpClient();
        return ToProbeResult(await CheckGitHubMembershipWithTokenAsync(http, tokenResult.Stdout.Trim(), cancellationToken).ConfigureAwait(false));
    }

    private async Task<InternalMicrosoftProbeResult> CheckWslWindowsGhCliAsync(CancellationToken cancellationToken)
    {
        if (IsCIEnvironment())
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        if (!CommandExists("gh.exe"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var tokenResult = await RunProcessAsync("gh.exe", ["auth", "token", "--hostname", "github.com"], cancellationToken).ConfigureAwait(false);
        if (GetProcessFailure(tokenResult, treatNonZeroExitAsFailure: false) is { } processFailure)
        {
            return processFailure;
        }

        if (tokenResult.ExitCode != 0 || string.IsNullOrWhiteSpace(tokenResult.Stdout))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        using var http = CreateGitHubHttpClient();
        return ToProbeResult(await CheckGitHubMembershipWithTokenAsync(http, tokenResult.Stdout.Trim(), cancellationToken).ConfigureAwait(false));
    }

    private async Task<InternalMicrosoftProbeResult> CheckEnvironmentGitHubTokenAsync(CancellationToken cancellationToken)
    {
        if (IsCIEnvironment())
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var tokenCandidates = DeduplicateTokenCandidates(GetGitHubTokenEnvironmentCandidates(cancellationToken));
        if (tokenCandidates.Count == 0)
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        return ToProbeResult(await CheckAnyGitHubMembershipCandidateAsync(tokenCandidates, cancellationToken).ConfigureAwait(false));
    }

    internal async Task<InternalMicrosoftProbeResult> CheckCopilotCliAsync(CancellationToken cancellationToken)
    {
        if (IsCIEnvironment())
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var tokenCandidates = new List<TokenCandidate>();
        foreach (var (name, value) in GetEnvironmentVariables())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (name.StartsWith("COPILOT_GH_ACCOUNT_", StringComparison.OrdinalIgnoreCase) && value is not null && LooksLikeGitHubToken(value))
            {
                tokenCandidates.Add(new TokenCandidate(value));
            }
        }

        var copilotHome = Path.Combine(_executionContext.HomeDirectory.FullName, ".copilot");
        foreach (var path in EnumerateExistingFiles(copilotHome, cancellationToken, "config.json", "settings.json"))
        {
            tokenCandidates.AddRange(ExtractGitHubTokenCandidates(path, cancellationToken));
        }

        tokenCandidates = DeduplicateTokenCandidates(tokenCandidates);
        if (tokenCandidates.Count == 0)
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        return ToProbeResult(await CheckAnyGitHubMembershipCandidateAsync(tokenCandidates, cancellationToken).ConfigureAwait(false));
    }

    private async Task<GitHubMembershipCheckResult> CheckAnyGitHubMembershipCandidateAsync(IReadOnlyList<TokenCandidate> candidates, CancellationToken cancellationToken)
    {
        var candidatesToCheck = candidates.Take(MaxGitHubTokenCandidates).ToArray();
        if (candidatesToCheck.Length == 0)
        {
            return GitHubMembershipCheckResult.NotMember;
        }

        using var timeoutSource = new CancellationTokenSource(_gitHubCandidateTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var candidateTasks = candidatesToCheck
            .Select(candidate => CheckGitHubMembershipCandidateAsync(candidate, linkedSource.Token))
            .ToList();
        InternalMicrosoftProbeFailure? failure = null;

        try
        {
            while (candidateTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(candidateTasks).WaitAsync(linkedSource.Token).ConfigureAwait(false);
                candidateTasks.Remove(completedTask);

                var result = await completedTask.ConfigureAwait(false);
                if (result.IsMember)
                {
                    await linkedSource.CancelAsync().ConfigureAwait(false);
                    await DrainGitHubCandidateTasksAsync(candidateTasks).ConfigureAwait(false);
                    return result;
                }
                failure ??= result.Failure;
            }

            return failure is null
                ? GitHubMembershipCheckResult.NotMember
                : new GitHubMembershipCheckResult(IsMember: false, Failure: failure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            return new GitHubMembershipCheckResult(
                IsMember: false,
                new InternalMicrosoftProbeFailure(
                    InternalMicrosoftProbeFailureCode.RequestFailed,
                    InternalMicrosoftProbeFailureStage.GitHubCandidates,
                    ExceptionType: InternalMicrosoftProbeExceptionType.TaskCanceled));
        }
        finally
        {
            await linkedSource.CancelAsync().ConfigureAwait(false);
            await DrainGitHubCandidateTasksAsync(candidateTasks).ConfigureAwait(false);
        }
    }

    private async Task<GitHubMembershipCheckResult> CheckGitHubMembershipCandidateAsync(TokenCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            using var http = CreateGitHubHttpClient();
            return await CheckGitHubMembershipWithTokenAsync(http, candidate.Token, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "GitHub token membership probe failed.");
            }

            return new GitHubMembershipCheckResult(
                IsMember: false,
                new InternalMicrosoftProbeFailure(
                    InternalMicrosoftProbeFailureCode.RequestFailed,
                    InternalMicrosoftProbeFailureStage.GitHubCandidates,
                    ExceptionType: GetSafeExceptionType(ex)));
        }
    }

    private async Task DrainGitHubCandidateTasksAsync(IReadOnlyList<Task<GitHubMembershipCheckResult>> candidateTasks)
    {
        if (candidateTasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(candidateTasks).WaitAsync(s_cancelledProbeDrainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "A cancelled GitHub token membership probe failed while draining.");
            }
        }
    }

    internal async Task<bool> CheckGitHubMembershipWithTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var http = CreateGitHubHttpClient();
        return (await CheckGitHubMembershipWithTokenAsync(http, token, cancellationToken).ConfigureAwait(false)).IsMember;
    }

    internal async Task<InternalMicrosoftProbeResult> CheckGitHubMembershipWithTokenResultForTestingAsync(string token, CancellationToken cancellationToken)
    {
        using var http = CreateGitHubHttpClient();
        return ToProbeResult(await CheckGitHubMembershipWithTokenAsync(http, token, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<GitHubMembershipCheckResult> CheckGitHubMembershipWithTokenAsync(HttpClient http, string token, CancellationToken cancellationToken)
    {
        using var userRequest = NewGitHubRequest(HttpMethod.Get, "https://api.github.com/user", token);
        using var userResponse = await http.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);
        if (!userResponse.IsSuccessStatusCode)
        {
            return HttpFailure(userResponse.StatusCode, InternalMicrosoftProbeFailureStage.GitHubUser);
        }

        string? login;
        try
        {
            login = await ReadJsonPropertyAsync(userResponse, "login", cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return JsonFailure(InternalMicrosoftProbeFailureStage.GitHubUser, parseFailure: true);
        }
        if (string.IsNullOrWhiteSpace(login))
        {
            return JsonFailure(InternalMicrosoftProbeFailureStage.GitHubUser, parseFailure: false);
        }

        using var membershipRequest = NewGitHubRequest(HttpMethod.Get, $"https://api.github.com/user/memberships/orgs/{MicrosoftGitHubOrg}", token);
        using var membershipResponse = await http.SendAsync(membershipRequest, cancellationToken).ConfigureAwait(false);
        if (membershipResponse.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(await membershipResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                var state = TryGetString(doc.RootElement, "state");
                return state is null
                    ? JsonFailure(InternalMicrosoftProbeFailureStage.GitHubMembership, parseFailure: false)
                    : new GitHubMembershipCheckResult(state.Equals("active", StringComparison.OrdinalIgnoreCase));
            }
            catch (JsonException)
            {
                return JsonFailure(InternalMicrosoftProbeFailureStage.GitHubMembership, parseFailure: true);
            }
        }
        if (membershipResponse.StatusCode != HttpStatusCode.NotFound)
        {
            return HttpFailure(membershipResponse.StatusCode, InternalMicrosoftProbeFailureStage.GitHubMembership);
        }

        using var publicMemberRequest = NewGitHubRequest(HttpMethod.Get, $"https://api.github.com/orgs/{MicrosoftGitHubOrg}/public_members/{login}", token);
        using var publicMemberResponse = await http.SendAsync(publicMemberRequest, cancellationToken).ConfigureAwait(false);
        return publicMemberResponse.StatusCode == HttpStatusCode.NoContent
            ? new GitHubMembershipCheckResult(IsMember: true)
            : publicMemberResponse.StatusCode == HttpStatusCode.NotFound
                ? GitHubMembershipCheckResult.NotMember
                : HttpFailure(publicMemberResponse.StatusCode, InternalMicrosoftProbeFailureStage.GitHubPublicMembership);
    }

    private static InternalMicrosoftProbeResult ToProbeResult(GitHubMembershipCheckResult result)
        => result.IsMember
            ? Detected(alias: null)
            : result.Failure is not null
                ? InternalMicrosoftProbeResult.Failed(result.Failure)
                : InternalMicrosoftProbeResult.NotDetected;

    private static GitHubMembershipCheckResult HttpFailure(HttpStatusCode statusCode, string stage)
        => new(
            IsMember: false,
            new InternalMicrosoftProbeFailure(
                InternalMicrosoftProbeFailureCode.HttpStatus,
                stage,
                HttpStatusCode: (int)statusCode));

    private static GitHubMembershipCheckResult JsonFailure(string stage, bool parseFailure)
        => new(
            IsMember: false,
            new InternalMicrosoftProbeFailure(
                parseFailure ? InternalMicrosoftProbeFailureCode.JsonParse : InternalMicrosoftProbeFailureCode.JsonShape,
                stage,
                ExceptionType: parseFailure ? InternalMicrosoftProbeExceptionType.Json : null));

    private HttpClient CreateGitHubHttpClient()
    {
        var http = _gitHubHttpMessageHandler is null
            ? new HttpClient()
            : new HttpClient(_gitHubHttpMessageHandler, disposeHandler: false);
        http.Timeout = _gitHubHttpTimeout;

        http.DefaultRequestHeaders.UserAgent.ParseAdd("aspire-cli-internal-microsoft-detector/1.0");
        return http;
    }

    private static HttpRequestMessage NewGitHubRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static async Task<string?> ReadJsonPropertyAsync(HttpResponseMessage response, string propertyName, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return TryGetString(doc.RootElement, propertyName);
    }

    private async Task<ProcessResult> RunProcessAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var options = new ProcessInvocationOptions
        {
            SuppressLogging = true,
            StandardOutputCallback = line => stdout.AppendLine(line),
            StandardErrorCallback = line => stderr.AppendLine(line)
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_processProbeTimeout);
        await using var execution = _processExecutionFactory.CreateExecution(
            fileName,
            arguments,
            env: null,
            _executionContext.WorkingDirectory,
            options);

        var started = false;
        try
        {
            if (!await execution.StartAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                return new ProcessResult(
                    ExitCode: -1,
                    stdout.ToString(),
                    stderr.ToString(),
                    new InternalMicrosoftProbeFailure(
                        InternalMicrosoftProbeFailureCode.ProcessStart,
                        InternalMicrosoftProbeFailureStage.ProcessStart));
            }

            started = true;
            var exitCode = await execution.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProcessResult(
                ExitCode: -1,
                stdout.ToString(),
                stderr.ToString(),
                new InternalMicrosoftProbeFailure(
                    InternalMicrosoftProbeFailureCode.ProcessTimeout,
                    started ? InternalMicrosoftProbeFailureStage.ProcessExit : InternalMicrosoftProbeFailureStage.ProcessStart));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProcessResult(
                ExitCode: -1,
                stdout.ToString(),
                stderr.ToString(),
                CreateExceptionFailure(
                    ex,
                    started ? InternalMicrosoftProbeFailureStage.ProcessExit : InternalMicrosoftProbeFailureStage.ProcessStart));
        }
    }

    private static InternalMicrosoftProbeResult? GetProcessFailure(ProcessResult result, bool treatNonZeroExitAsFailure)
    {
        if (result.Failure is not null)
        {
            return InternalMicrosoftProbeResult.Failed(result.Failure);
        }

        return treatNonZeroExitAsFailure && result.ExitCode != 0
            ? InternalMicrosoftProbeResult.Failed(new(
                InternalMicrosoftProbeFailureCode.ProcessExit,
                InternalMicrosoftProbeFailureStage.ProcessExit,
                ProcessExitCode: result.ExitCode))
            : null;
    }

    private static InternalMicrosoftProbeResult EvaluateWindowsWorkplaceJoin(string output, string? fallbackDomain)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var values = ParseColonSeparatedFields(output);
        var tenantId = values.GetValueOrDefault("TenantId");
        var azureAdJoined = IsYes(values.GetValueOrDefault("AzureAdJoined"));
        var workplaceJoined = IsYes(values.GetValueOrDefault("WorkplaceJoined"));
        var alias = ExtractAliasFromAccountIdentifier(GetFirstValue(values, "UserEmail", "User Email", "UserPrincipalName", "User Principal Name", "UPN"));
        var domain = ExtractAdDomainNameFromDsReg(values) ?? ExtractAdDomainNameFromCorpDnsName(fallbackDomain);

        return (azureAdJoined || workplaceJoined) && tenantId?.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase) == true
            ? Detected(alias, domain)
            : InternalMicrosoftProbeResult.NotDetected;

        static bool IsYes(string? value)
        {
            return value?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    private static Dictionary<string, string> ParseColonSeparatedFields(string text)
    {
        // dsregcmd /status writes colon-separated sections, e.g.:
        //   AzureAdJoined : YES
        //   TenantId : 72f988bf-86f1-41af-91ab-2d7cd011db47
        //   User Email : alias@microsoft.com
        // Values can contain additional ':' characters, so split only on the first delimiter.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf(':', StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static InternalMicrosoftProbeResult DetectVisualStudioMicrosoftTenant(string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        try
        {
            using var store = JsonDocument.Parse(text);
            if (store.RootElement.ValueKind != JsonValueKind.Array)
            {
                return JsonShapeFailure(InternalMicrosoftProbeFailureStage.AccountStore);
            }

            var personalizationResults = new List<InternalMicrosoftProbeResult>();
            var fallbackResults = new List<InternalMicrosoftProbeResult>();
            var hasMalformedPersonalizationAccount = false;
            var hasMalformedFallbackAccount = false;
            InternalMicrosoftProbeResult? failure = null;
            foreach (var account in store.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = TryDetectVisualStudioMicrosoftTenantAccount(account);
                if (result.Failure is not null)
                {
                    failure ??= result;
                    if (HasMicrosoftIdentityProvider(account))
                    {
                        if (TryGetBoolean(account, "IsPersonalizationAccount") == true)
                        {
                            hasMalformedPersonalizationAccount = true;
                        }
                        else
                        {
                            hasMalformedFallbackAccount = true;
                        }
                    }
                    continue;
                }
                if (!result.IsInternalMicrosoft)
                {
                    continue;
                }

                if (TryGetBoolean(account, "IsPersonalizationAccount") == true)
                {
                    personalizationResults.Add(result);
                }
                else
                {
                    fallbackResults.Add(result);
                }
            }

            if (personalizationResults.Count > 0)
            {
                return hasMalformedPersonalizationAccount
                    ? Detected(alias: null)
                    : SelectUnambiguousIdentity(personalizationResults);
            }

            if (fallbackResults.Count > 0)
            {
                return hasMalformedPersonalizationAccount || hasMalformedFallbackAccount
                    ? Detected(alias: null)
                    : SelectUnambiguousIdentity(fallbackResults);
            }

            return failure ?? InternalMicrosoftProbeResult.NotDetected;
        }
        catch (JsonException)
        {
            return InternalMicrosoftProbeResult.Failed(new(
                InternalMicrosoftProbeFailureCode.JsonParse,
                InternalMicrosoftProbeFailureStage.AccountStore,
                ExceptionType: InternalMicrosoftProbeExceptionType.Json));
        }
    }

    private static bool HasMicrosoftIdentityProvider(JsonElement account)
    {
        return account.ValueKind == JsonValueKind.Object &&
            account.TryGetProperty("Properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object &&
            TryGetString(properties, "IdentityProvider")?.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static InternalMicrosoftProbeResult SelectUnambiguousIdentity(IReadOnlyList<InternalMicrosoftProbeResult> candidates)
    {
        var first = candidates[0];
        return candidates.Skip(1).All(candidate =>
            string.Equals(candidate.Alias, first.Alias, StringComparison.Ordinal) &&
            string.Equals(candidate.Domain, first.Domain, StringComparison.Ordinal))
                ? first
                : Detected(alias: null);
    }

    private static InternalMicrosoftProbeResult TryDetectVisualStudioMicrosoftTenantAccount(JsonElement account)
    {
        // Visual Studio's internal V3 account store currently contains records shaped like:
        //   [{
        //     "Stale": false,
        //     "IsPersonalizationAccount": true,
        //     "Properties": {
        //       "IdentityProvider": "<tenant-guid>",
        //       "HomeTenant": "<tenant-guid>",
        //       "IdTokenPayload": "{\"tid\":\"<tenant-guid>\",\"iss\":\"https://login.microsoftonline.com/<tenant-guid>/v2.0\",\"preferred_username\":\"alias@microsoft.com\"}"
        //     }
        //   }]
        // The format is not a supported Visual Studio contract, so require every piece of tenant
        // and alias evidence to be structurally bound to one non-stale record and fail closed when
        // the shape changes. IsPersonalizationAccount is used only to rank matching records.
        if (account.ValueKind != JsonValueKind.Object)
        {
            return JsonShapeFailure(InternalMicrosoftProbeFailureStage.AccountStoreRecord);
        }

        var stale = TryGetBoolean(account, "Stale");
        if (stale is null)
        {
            return JsonShapeFailure(InternalMicrosoftProbeFailureStage.AccountStoreRecordStale);
        }
        if (stale.Value)
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        if (!account.TryGetProperty("Properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return JsonShapeFailure(InternalMicrosoftProbeFailureStage.AccountStoreRecordProperties);
        }

        var identityProvider = TryGetString(properties, "IdentityProvider");
        if (identityProvider is null)
        {
            return JsonShapeFailure(InternalMicrosoftProbeFailureStage.AccountStoreRecordIdentityProvider);
        }
        if (!identityProvider.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var homeTenant = TryGetString(properties, "HomeTenant");
        if (homeTenant is null)
        {
            return JsonShapeFailure(InternalMicrosoftProbeFailureStage.AccountStoreRecordHomeTenant);
        }
        if (!homeTenant.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        if (TryGetString(properties, "IdTokenPayload") is not { } idTokenPayload)
        {
            return JsonShapeFailure(InternalMicrosoftProbeFailureStage.IdTokenPayload);
        }

        try
        {
            using var token = JsonDocument.Parse(idTokenPayload);
            var expectedIssuer = $"https://login.microsoftonline.com/{MicrosoftTenantId}/v2.0";
            if (token.RootElement.ValueKind != JsonValueKind.Object)
            {
                return JsonShapeFailure(InternalMicrosoftProbeFailureStage.IdTokenPayload);
            }

            var tenant = TryGetString(token.RootElement, "tid");
            if (tenant is null)
            {
                return JsonShapeFailure(InternalMicrosoftProbeFailureStage.IdTokenTenant);
            }
            if (!tenant.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase))
            {
                return InternalMicrosoftProbeResult.NotDetected;
            }

            var issuer = TryGetString(token.RootElement, "iss");
            if (issuer is null)
            {
                return JsonShapeFailure(InternalMicrosoftProbeFailureStage.IdTokenIssuer);
            }
            if (!issuer.Equals(expectedIssuer, StringComparison.OrdinalIgnoreCase))
            {
                return InternalMicrosoftProbeResult.NotDetected;
            }

            if (ExtractAliasFromAccountIdentifier(TryGetString(token.RootElement, "preferred_username")) is not { } alias)
            {
                return JsonShapeFailure(InternalMicrosoftProbeFailureStage.IdTokenUsername);
            }

            return Detected(alias);
        }
        catch (JsonException)
        {
            return InternalMicrosoftProbeResult.Failed(new(
                InternalMicrosoftProbeFailureCode.JsonParse,
                InternalMicrosoftProbeFailureStage.IdTokenPayload,
                ExceptionType: InternalMicrosoftProbeExceptionType.Json));
        }
    }

    private static InternalMicrosoftProbeResult JsonShapeFailure(string stage)
        => InternalMicrosoftProbeResult.Failed(new(InternalMicrosoftProbeFailureCode.JsonShape, stage));

    internal static InternalMicrosoftProbeResult DetectVisualStudioMicrosoftTenantForTesting(string text, CancellationToken cancellationToken)
        => DetectVisualStudioMicrosoftTenant(text, cancellationToken);

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? property.GetBoolean()
                : null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private IEnumerable<TokenCandidate> GetGitHubTokenEnvironmentCandidates(CancellationToken cancellationToken)
    {
        var exactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GH_TOKEN",
            "GITHUB_TOKEN",
            "GITHUB_PAT",
            "GITHUB_OAUTH_TOKEN",
            "GITHUB_ACCESS_TOKEN"
        };

        foreach (var (name, value) in GetEnvironmentVariables())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (exactNames.Contains(name) && value is not null && LooksLikeGitHubToken(value))
            {
                yield return new TokenCandidate(value);
            }
        }
    }

    private static List<TokenCandidate> DeduplicateTokenCandidates(IEnumerable<TokenCandidate> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TokenCandidate>();

        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate.Token))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static IEnumerable<TokenCandidate> ExtractGitHubTokenCandidates(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            yield break;
        }

        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in GitHubTokenRegex().Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var token = match.Value;
            if (LooksLikeGitHubToken(token))
            {
                yield return new TokenCandidate(token);
            }
        }
    }

    private static IEnumerable<string> EnumerateExistingFiles(string directory, CancellationToken cancellationToken, params string[] fileNames)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var fileName in fileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private bool IsWsl()
    {
        if (!_environment.IsLinux())
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
            !string.IsNullOrWhiteSpace(_environment.GetEnvironmentVariable("WSL_INTEROP")))
        {
            return true;
        }

        try
        {
            if (!File.Exists("/proc/sys/kernel/osrelease"))
            {
                return false;
            }

            var osRelease = File.ReadAllText("/proc/sys/kernel/osrelease");
            return osRelease.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
                osRelease.Contains("wsl", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsCIEnvironment()
        => _ciEnvironmentDetector.IsCIEnvironment();

    private bool CommandExists(string command)
    {
        var path = _environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extensions = _environment.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(command))
            ? (_environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string GetSpecialFolderPath(Environment.SpecialFolder folder, string environmentVariableName)
    {
        return _environment.GetEnvironmentVariable(environmentVariableName) ??
            Environment.GetFolderPath(folder);
    }

    private IEnumerable<(string Name, string? Value)> GetEnvironmentVariables()
    {
        return _environment.GetEnvironmentVariables();
    }

    private static JsonObject? TryParseJsonObject(string text)
    {
        try
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start >= 0 && end > start
                ? JsonNode.Parse(text[start..(end + 1)], documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static InternalMicrosoftProbeResult Detected(string? alias, string? domain = null)
    {
        return new InternalMicrosoftProbeResult(IsInternalMicrosoft: true, Alias: NormalizeAlias(alias), Domain: NormalizeAdDomainName(domain));
    }

    private static string? GetFirstValue(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ExtractAdDomainNameFromDsReg(IReadOnlyDictionary<string, string> values)
    {
        var domain = GetFirstValue(
            values,
            "DomainName",
            "Domain Name",
            "OnPremisesDomainName",
            "On Premises Domain Name",
            "OnPremDomainName",
            "UserDnsDomain",
            "User DNS Domain");

        return ExtractAdDomainNameFromCorpDnsName(domain) ?? NormalizeAdDomainName(domain);
    }

    private static string? ExtractAdDomainNameFromCorpDnsName(string? dnsDomain)
    {
        if (string.IsNullOrWhiteSpace(dnsDomain))
        {
            return null;
        }

        var trimmed = dnsDomain.Trim().TrimEnd('.');
        if (!trimmed.EndsWith(CorpMicrosoftDomainSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return NormalizeAdDomainName(trimmed[..^CorpMicrosoftDomainSuffix.Length]);
    }

    private static string? NormalizeAdDomainName(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var normalized = domain.Trim().TrimEnd('.');
        if (normalized.EndsWith(CorpMicrosoftDomainSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return ExtractAdDomainNameFromCorpDnsName(normalized);
        }

        return normalized.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-')
            ? normalized.ToUpperInvariant()
            : null;
    }

    private static string? ExtractAliasFromAccountIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = MicrosoftAccountRegex().Match(value);
        return match.Success && match.Index == 0 && match.Length == value.Length
            ? NormalizeAlias(match.Groups["alias"].Value)
            : null;
    }

    private static string? ExtractAdDomainNameFromAccountIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var atIndex = value.LastIndexOf('@');
        return atIndex >= 0 && atIndex < value.Length - 1
            ? ExtractAdDomainNameFromCorpDnsName(value[(atIndex + 1)..])
            : null;
    }

    private static string? NormalizeAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var normalized = alias.Trim();
        return normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            ? normalized.ToLowerInvariant()
            : null;
    }

    private static bool HasJsonStringProperty(JsonObject json, string propertyName, string expectedValue)
    {
        return TryGetString(json, propertyName)?.Equals(expectedValue, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? TryGetString(JsonObject json, string propertyName)
    {
        return json.TryGetPropertyValue(propertyName, out var value) &&
            value is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static bool LooksLikeGitHubToken(string token)
    {
        return GitHubTokenRegex().IsMatch(token);
    }

    [GeneratedRegex(@"(?:github_pat_[A-Za-z0-9_]{20,}|gh[opsru]_[A-Za-z0-9_]{20,})")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._%+\-\\])(?<alias>[A-Za-z0-9._%+-]+)@(?<domain>(?:[A-Za-z0-9-]+\.)*microsoft\.com)(?![A-Za-z0-9._%+-])", RegexOptions.IgnoreCase)]
    private static partial Regex MicrosoftAccountRegex();

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr, InternalMicrosoftProbeFailure? Failure = null);
    private readonly record struct TokenCandidate(string Token);
    private readonly record struct GitHubMembershipCheckResult(bool IsMember, InternalMicrosoftProbeFailure? Failure = null)
    {
        public static GitHubMembershipCheckResult NotMember { get; } = new(IsMember: false);
    }
    private sealed record InternalMicrosoftCacheReadResult(InternalMicrosoftDetectorCacheEntry? Entry, string CacheStatus);
    private sealed record InternalMicrosoftProbeStageResult(InternalMicrosoftDetectionResult? Result, IReadOnlyList<InternalMicrosoftProbeDiagnostic> Diagnostics, bool TimedOut);
    private sealed record InternalMicrosoftProbeRunResult(InternalMicrosoftProbe Probe, string Source, InternalMicrosoftProbeResult Result, InternalMicrosoftProbeDiagnostic Diagnostic, long CompletionTimestamp);
}

internal sealed record InternalMicrosoftProbe(
    string Name,
    Func<CancellationToken, Task<InternalMicrosoftProbeResult>> DetectAsync,
    TimeSpan? DurationOverride = null);

internal readonly record struct InternalMicrosoftProbeResult(bool IsInternalMicrosoft, string? Alias, string? Domain, InternalMicrosoftProbeFailure? Failure = null)
{
    public static InternalMicrosoftProbeResult NotDetected { get; } = new(IsInternalMicrosoft: false, Alias: null, Domain: null);

    public static InternalMicrosoftProbeResult Failed(InternalMicrosoftProbeFailure failure)
        => new(IsInternalMicrosoft: false, Alias: null, Domain: null, Failure: failure);
}

internal static class InternalMicrosoftDetectorOutcome
{
    public const string Detected = "detected";
    public const string NotDetected = "not_detected";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
}

internal static class InternalMicrosoftDetectorCacheStatus
{
    public const string Hit = "hit";
    public const string Miss = "miss";
    public const string Stale = "stale";
}

internal static class InternalMicrosoftProbeOutcome
{
    public const string Detected = "detected";
    public const string NotDetected = "not_detected";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

internal sealed record InternalMicrosoftProbeDiagnostic(string Source, string Outcome, TimeSpan Duration, bool HasAlias, bool HasDomain, InternalMicrosoftProbeFailure? Failure = null);

internal sealed record InternalMicrosoftProbeFailure(
    string Code,
    string Stage,
    string? ExceptionType = null,
    int? ProcessExitCode = null,
    int? HttpStatusCode = null);

internal static class InternalMicrosoftProbeFailureCode
{
    public const string Exception = "exception";
    public const string FileUnreadable = "file_unreadable";
    public const string HttpStatus = "http_status";
    public const string JsonParse = "json_parse";
    public const string JsonShape = "json_shape";
    public const string ProcessExit = "process_exit";
    public const string ProcessStart = "process_start";
    public const string ProcessTimeout = "process_timeout";
    public const string RequestFailed = "request_failed";
}

internal static class InternalMicrosoftProbeFailureStage
{
    public const string AccountStore = "account_store";
    public const string AccountStoreRecord = "account_store_record";
    public const string AccountStoreRecordHomeTenant = "account_store_record.home_tenant";
    public const string AccountStoreRecordIdentityProvider = "account_store_record.identity_provider";
    public const string AccountStoreRecordProperties = "account_store_record.properties";
    public const string AccountStoreRecordStale = "account_store_record.stale";
    public const string GitHubCandidates = "github_candidates";
    public const string GitHubMembership = "github_membership";
    public const string GitHubPublicMembership = "github_public_membership";
    public const string GitHubUser = "github_user";
    public const string IdTokenPayload = "id_token_payload";
    public const string IdTokenIssuer = "id_token_payload.iss";
    public const string IdTokenTenant = "id_token_payload.tid";
    public const string IdTokenUsername = "id_token_payload.preferred_username";
    public const string PlatformSso = "platform_sso";
    public const string ProcessExit = "process_exit";
    public const string ProcessStart = "process_start";
    public const string Probe = "probe";
}

internal static class InternalMicrosoftProbeExceptionType
{
    public const string HttpRequest = "HttpRequestException";
    public const string InvalidOperation = "InvalidOperationException";
    public const string Io = "IOException";
    public const string Json = "JsonException";
    public const string Other = "Other";
    public const string TaskCanceled = "TaskCanceledException";
    public const string UnauthorizedAccess = "UnauthorizedAccessException";
}

internal sealed record InternalMicrosoftDetectionResult(
    bool IsInternalMicrosoft,
    string? Source,
    string? Alias,
    string? Domain,
    string Outcome,
    string CacheStatus,
    TimeSpan Duration,
    IReadOnlyList<InternalMicrosoftProbeDiagnostic> ProbeDiagnostics)
{
    public InternalMicrosoftDetectionResult(bool IsInternalMicrosoft, string? Source, string? Alias, string? Domain)
        : this(
            IsInternalMicrosoft,
            Source,
            Alias,
            Domain,
            IsInternalMicrosoft ? InternalMicrosoftDetectorOutcome.Detected : InternalMicrosoftDetectorOutcome.NotDetected,
            InternalMicrosoftDetectorCacheStatus.Miss,
            TimeSpan.Zero,
            [])
    {
    }
}

internal sealed record InternalMicrosoftDetectorCacheEntry
{
    public int Version { get; init; }
    public bool IsInternalMicrosoft { get; init; }
    public string? Source { get; init; }
    public string? Alias { get; init; }
    public string? Domain { get; init; }
    public bool IsCIEnvironment { get; init; }
    public DateTimeOffset LastRunUtc { get; init; }
}
