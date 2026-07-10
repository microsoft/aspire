// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    private const int MaxGitHubTokenCandidates = 5;

    private static readonly TimeSpan s_cacheRefreshInterval = TimeSpan.FromHours(6);
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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InternalMicrosoftDetector> _logger;
    private readonly IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> _probeStages;

    public InternalMicrosoftDetector(CliExecutionContext executionContext, IEnvironment environment, TimeProvider timeProvider, ILogger<InternalMicrosoftDetector> logger, IProcessExecutionFactory processExecutionFactory)
        : this(
            executionContext,
            environment,
            Path.Combine(executionContext.CacheDirectory.FullName, CacheSubdirectoryName, CacheFileName),
            timeProvider,
            logger,
            processExecutionFactory,
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
        IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>>? probeStages,
        HttpMessageHandler? gitHubHttpMessageHandler = null,
        TimeSpan? gitHubCandidateTimeout = null)
    {
        _cacheFilePath = cacheFilePath;
        _executionContext = executionContext;
        _environment = environment;
        _processExecutionFactory = processExecutionFactory;
        _gitHubHttpMessageHandler = gitHubHttpMessageHandler;
        _gitHubCandidateTimeout = gitHubCandidateTimeout ?? s_gitHubCandidateTimeout;
        _timeProvider = timeProvider;
        _logger = logger;
        _probeStages = probeStages ?? CreateDefaultProbeStages();
    }

    public async Task<InternalMicrosoftDetectionResult> IsInternalMicrosoftMachineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cached = await TryReadFreshCacheAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return new InternalMicrosoftDetectionResult(cached.IsInternalMicrosoft, cached.Source, cached.Alias, cached.Domain);
            }

            var result = await RunProbeStagesAsync(cancellationToken).ConfigureAwait(false) ??
                new InternalMicrosoftDetectionResult(IsInternalMicrosoft: false, Source: null, Alias: null, Domain: null);
            await TryWriteCacheAsync(result, cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Internal Microsoft detection failed.");
            }

            return new InternalMicrosoftDetectionResult(IsInternalMicrosoft: false, Source: null, Alias: null, Domain: null);
        }
    }

    private IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> CreateDefaultProbeStages()
    {
        // Probes are ordered by cost and signal quality. Local account stores and OS enrollment
        // state come from standard developer-machine tooling: Windows dsregcmd, Visual Studio
        // IdentityService, VS Code global state, macOS Platform SSO, gh/Copilot CLI auth, and
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

        // Probes that may require file I/O or process execution, but can still complete relatively quickly
        var stage2 = new List<InternalMicrosoftProbe>
        {
            // Is the user signed into VS Code with a Microsoft account that belongs to the Microsoft tenant?
            new("VS Code Microsoft tenant", CheckVsCodeMicrosoftTenantAsync)
        };

        // Probes that may involve more extensive process execution/network calls or are a weaker signal, run last
        // to avoid delaying detection when faster/better quality signals are available
        var stage3 = new List<InternalMicrosoftProbe>
        {
            // Is there a GitHub token in the environment that has an active membership in the Microsoft GitHub org?
            new("Environment GitHub token membership", CheckEnvironmentGitHubTokenAsync),

            // Is there a GitHub token from the gh CLI that has an active membership in the Microsoft GitHub org?
            new("gh CLI GitHub org membership", CheckGhCliAsync),

            // Is there a GitHub token from the Copilot CLI that has an active membership in the Microsoft GitHub org?
            new("Copilot CLI GitHub org membership", CheckCopilotCliAsync)
        };

        if (_environment.IsWindows())
        {
            // Stage 1

            // Check USERDNSDOMAIN for a corp.microsoft.com domain, which is a strong signal of being on a Microsoft corporate machine or VPN.
            // This is much faster than checking workplace join status and doesn't require admin privileges, so we check it in stage 1.
            stage1.Add(new("Windows USERDNSDOMAIN", CheckWindowsUserDnsDomainAsync));

            // Check for a Microsoft tenant in the Visual Studio account store, which is a strong signal of being a Microsoft employee.
            // This is also relatively fast and doesn't require admin privileges, so we check it in stage 1.
            stage1.Add(new("Visual Studio Microsoft tenant", CheckVisualStudioMicrosoftTenantAsync));

            // Stage 3

            // Check if the machine is workplace joined to the Microsoft tenant, which is a strong signal of being on a Microsoft corporate machine,
            // but can be slower to evaluate so we check it in stage 3.
            stage3.Add(new("Windows workplace join", CheckWindowsWorkplaceJoinAsync));
        }
        else if (IsWsl())
        {
            // Stage 1

            // Check USERDNSDOMAIN for a corp.microsoft.com domain on the Windows host, which is a strong signal of being on a Microsoft corporate machine or VPN.
            // This is much faster than checking workplace join status and doesn't require admin privileges, so we check it in stage 1.
            stage1.Add(new("WSL Windows USERDNSDOMAIN", CheckWslWindowsUserDnsDomainAsync));

            // Check for a Microsoft tenant in the Visual Studio account store on the Windows host, which is a strong signal of being a Microsoft employee.
            // This is also relatively fast and doesn't require admin privileges, so we check it in stage 1.
            stage1.Add(new("WSL Visual Studio Microsoft tenant", CheckWslVisualStudioMicrosoftTenantAsync));

            // Stage 3

            // Check if the Windows host machine is workplace joined to the Microsoft tenant, which is a strong signal of being on a Microsoft corporate machine,
            // but can be slower to evaluate so we check it in stage 3.
            stage3.Add(new("WSL Windows workplace join", CheckWslWindowsWorkplaceJoinAsync));
            stage3.Add(new("WSL Windows gh.exe GitHub org membership", CheckWslWindowsGhCliAsync));
        }

        return [stage1, stage2, stage3];
    }

    private async Task<InternalMicrosoftDetectionResult?> RunProbeStagesAsync(CancellationToken cancellationToken)
    {
        foreach (var stage in _probeStages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stage.Count == 0)
            {
                continue;
            }

            var result = await RunProbeStageAsync(stage, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private async Task<InternalMicrosoftDetectionResult?> RunProbeStageAsync(IReadOnlyList<InternalMicrosoftProbe> probes, CancellationToken cancellationToken)
    {
        using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeTasks = probes.Select(probe => RunProbeAsync(probe, stageCancellation.Token)).ToList();
        var pendingTasks = probeTasks.ToList();

        while (pendingTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingTasks).ConfigureAwait(false);
            pendingTasks.Remove(completedTask);

            var result = await completedTask.ConfigureAwait(false);
            if (result is not null)
            {
                await stageCancellation.CancelAsync().ConfigureAwait(false);
                await DrainCancelledProbesAsync(probeTasks).ConfigureAwait(false);
                return result;
            }
        }

        return null;
    }

    private Task<InternalMicrosoftDetectionResult?> RunProbeAsync(InternalMicrosoftProbe probe, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await probe.DetectAsync(cancellationToken).ConfigureAwait(false);
                return result.IsInternalMicrosoft
                    ? new InternalMicrosoftDetectionResult(IsInternalMicrosoft: true, Source: probe.Name, Alias: result.Alias, Domain: result.Domain)
                    : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(ex, "Microsoft internal probe '{ProbeName}' failed.", probe.Name);
                }
                return null;
            }
        }, CancellationToken.None);
    }

    private async Task DrainCancelledProbesAsync(IReadOnlyList<Task<InternalMicrosoftDetectionResult?>> probeTasks)
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

    private async Task<InternalMicrosoftDetectorCacheEntry?> TryReadFreshCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cacheFilePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InternalMicrosoftDetectorCacheEntry);
            if (entry is null)
            {
                return null;
            }

            var hasRequiredSource = !entry.IsInternalMicrosoft || !string.IsNullOrEmpty(entry.Source);
            return hasRequiredSource && _timeProvider.GetUtcNow() - entry.LastRunUtc < s_cacheRefreshInterval
                ? entry
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Failed to read Microsoft internal detector cache from {CacheFilePath}.", _cacheFilePath);
            }
            return null;
        }
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
                IsInternalMicrosoft = result.IsInternalMicrosoft,
                Source = result.Source,
                Alias = result.Alias,
                Domain = result.Domain,
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

        var text = await FileSystemHelper.TryReadAllTextAsync(accountStore, cancellationToken).ConfigureAwait(false);
        return DetectMicrosoftTenant(text, cancellationToken);
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

        return result.ExitCode == 0
            ? DetectMicrosoftTenant(result.Stdout, cancellationToken)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    [SupportedOSPlatform("macos")]
    private async Task<InternalMicrosoftProbeResult> CheckMacPlatformSsoAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("app-sso"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("app-sso", ["platform", "-s"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        // app-sso emits a JSON document similar to:
        //   {"realm":"REDMOND.CORP.MICROSOFT.COM","upn":"alias@REDMOND.CORP.MICROSOFT.COM",
        //    "issuer":"https://login.microsoftonline.com/<tenant>/v2.0", ...}
        // Use JSON APIs for the fixed fields so formatting changes don't affect detection.
        var json = TryParseJsonObject($"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        if (json is null)
        {
            return InternalMicrosoftProbeResult.NotDetected;
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

    private async Task<InternalMicrosoftProbeResult> CheckVsCodeMicrosoftTenantAsync(CancellationToken cancellationToken)
    {
        foreach (var stateDatabasePath in GetVsCodeStateDatabasePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(stateDatabasePath))
            {
                continue;
            }

            var bytes = await FileSystemHelper.TryReadAllBytesAsync(stateDatabasePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(bytes);
            var result = DetectMicrosoftTenant(text, cancellationToken);
            if (result.IsInternalMicrosoft)
            {
                return result;
            }
        }

        return InternalMicrosoftProbeResult.NotDetected;
    }

    internal async Task<InternalMicrosoftProbeResult> CheckWindowsWorkplaceJoinAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("dsregcmd"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("dsregcmd", ["/status"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? EvaluateWindowsWorkplaceJoin(
                result.Stdout,
                _environment.GetEnvironmentVariable("USERNAME"),
                _environment.GetEnvironmentVariable("USERDNSDOMAIN"))
            : InternalMicrosoftProbeResult.NotDetected;
    }

    private async Task<InternalMicrosoftProbeResult> CheckWslWindowsWorkplaceJoinAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("cmd.exe", ["/c", "dsregcmd /status"], cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? EvaluateWindowsWorkplaceJoin(result.Stdout, fallbackAlias: null, fallbackDomain: null)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    private async Task<InternalMicrosoftProbeResult> CheckGhCliAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("gh"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var tokenResult = await RunProcessAsync("gh", ["auth", "token", "--hostname", "github.com"], cancellationToken).ConfigureAwait(false);
        if (tokenResult.ExitCode != 0 || string.IsNullOrWhiteSpace(tokenResult.Stdout))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        using var http = CreateGitHubHttpClient();
        return await CheckGitHubMembershipWithTokenAsync(http, tokenResult.Stdout.Trim(), cancellationToken).ConfigureAwait(false)
            ? Detected(alias: null)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    private async Task<InternalMicrosoftProbeResult> CheckWslWindowsGhCliAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("gh.exe"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var tokenResult = await RunProcessAsync("gh.exe", ["auth", "token", "--hostname", "github.com"], cancellationToken).ConfigureAwait(false);
        if (tokenResult.ExitCode != 0 || string.IsNullOrWhiteSpace(tokenResult.Stdout))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        using var http = CreateGitHubHttpClient();
        return await CheckGitHubMembershipWithTokenAsync(http, tokenResult.Stdout.Trim(), cancellationToken).ConfigureAwait(false)
            ? Detected(alias: null)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    private async Task<InternalMicrosoftProbeResult> CheckEnvironmentGitHubTokenAsync(CancellationToken cancellationToken)
    {
        var tokenCandidates = DeduplicateTokenCandidates(GetGitHubTokenEnvironmentCandidates(cancellationToken));
        if (tokenCandidates.Count == 0)
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        return await CheckAnyGitHubMembershipCandidateAsync(tokenCandidates, cancellationToken).ConfigureAwait(false)
            ? Detected(alias: null)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    internal async Task<InternalMicrosoftProbeResult> CheckCopilotCliAsync(CancellationToken cancellationToken)
    {
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

        return await CheckAnyGitHubMembershipCandidateAsync(tokenCandidates, cancellationToken).ConfigureAwait(false)
            ? Detected(alias: null)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    private async Task<bool> CheckAnyGitHubMembershipCandidateAsync(IReadOnlyList<TokenCandidate> candidates, CancellationToken cancellationToken)
    {
        var candidatesToCheck = candidates.Take(MaxGitHubTokenCandidates).ToArray();
        if (candidatesToCheck.Length == 0)
        {
            return false;
        }

        using var timeoutSource = new CancellationTokenSource(_gitHubCandidateTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var candidateTasks = candidatesToCheck
            .Select(candidate => CheckGitHubMembershipCandidateAsync(candidate, linkedSource.Token))
            .ToList();

        try
        {
            while (candidateTasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(candidateTasks).WaitAsync(linkedSource.Token).ConfigureAwait(false);
                candidateTasks.Remove(completedTask);

                if (await completedTask.ConfigureAwait(false))
                {
                    await linkedSource.CancelAsync().ConfigureAwait(false);
                    await DrainGitHubCandidateTasksAsync(candidateTasks).ConfigureAwait(false);
                    return true;
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            await linkedSource.CancelAsync().ConfigureAwait(false);
            await DrainGitHubCandidateTasksAsync(candidateTasks).ConfigureAwait(false);
        }
    }

    private async Task<bool> CheckGitHubMembershipCandidateAsync(TokenCandidate candidate, CancellationToken cancellationToken)
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

            return false;
        }
    }

    private async Task DrainGitHubCandidateTasksAsync(IReadOnlyList<Task<bool>> candidateTasks)
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
        return await CheckGitHubMembershipWithTokenAsync(http, token, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> CheckGitHubMembershipWithTokenAsync(HttpClient http, string token, CancellationToken cancellationToken)
    {
        using var userRequest = NewGitHubRequest(HttpMethod.Get, "https://api.github.com/user", token);
        using var userResponse = await http.SendAsync(userRequest, cancellationToken).ConfigureAwait(false);
        if (!userResponse.IsSuccessStatusCode)
        {
            return false;
        }

        var login = await ReadJsonPropertyAsync(userResponse, "login", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(login))
        {
            return false;
        }

        using var membershipRequest = NewGitHubRequest(HttpMethod.Get, $"https://api.github.com/user/memberships/orgs/{MicrosoftGitHubOrg}", token);
        using var membershipResponse = await http.SendAsync(membershipRequest, cancellationToken).ConfigureAwait(false);
        if (membershipResponse.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await membershipResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var state = TryGetString(doc.RootElement, "state");
            return state?.Equals("active", StringComparison.OrdinalIgnoreCase) == true;
        }

        using var publicMemberRequest = NewGitHubRequest(HttpMethod.Get, $"https://api.github.com/orgs/{MicrosoftGitHubOrg}/public_members/{login}", token);
        using var publicMemberResponse = await http.SendAsync(publicMemberRequest, cancellationToken).ConfigureAwait(false);
        return publicMemberResponse.StatusCode == HttpStatusCode.NoContent;
    }

    private HttpClient CreateGitHubHttpClient()
    {
        var http = _gitHubHttpMessageHandler is null
            ? new HttpClient()
            : new HttpClient(_gitHubHttpMessageHandler, disposeHandler: false);
        http.Timeout = s_gitHubHttpTimeout;

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

        if (!await execution.StartAsync(timeoutCts.Token).ConfigureAwait(false))
        {
            return new ProcessResult(ExitCode: -1, stdout.ToString(), stderr.ToString());
        }

        try
        {
            var exitCode = await execution.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProcessResult(ExitCode: -1, stdout.ToString(), stderr.ToString());
        }
    }

    private static InternalMicrosoftProbeResult EvaluateWindowsWorkplaceJoin(string output, string? fallbackAlias, string? fallbackDomain)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var values = ParseColonSeparatedFields(output);
        var tenantId = values.GetValueOrDefault("TenantId");
        var azureAdJoined = IsYes(values.GetValueOrDefault("AzureAdJoined"));
        var workplaceJoined = IsYes(values.GetValueOrDefault("WorkplaceJoined"));
        var alias = ExtractAliasFromAccountIdentifier(GetFirstValue(values, "UserEmail", "User Email", "UserPrincipalName", "User Principal Name", "UPN")) ??
            NormalizeAlias(fallbackAlias);
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

    private static InternalMicrosoftProbeResult DetectMicrosoftTenant(string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var alias = ExtractMicrosoftAccountAliasFromText(text, cancellationToken);
        if (text.Contains(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return Detected(alias);
        }

        foreach (var evidence in ExtractTenantAliasEvidenceFromJwtPayloads(text, cancellationToken))
        {
            if (evidence.TenantId.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase))
            {
                return Detected(evidence.Alias);
            }
        }

        return InternalMicrosoftProbeResult.NotDetected;
    }

    private static IEnumerable<TenantAliasEvidence> ExtractTenantAliasEvidenceFromJwtPayloads(string text, CancellationToken cancellationToken)
    {
        // Account stores often embed JWTs. Decode only the payload segment:
        //   base64url(header).base64url(payload).base64url(signature)
        // and look for tenant/user claims such as tid, tenantId, preferred_username, and upn.
        var evidence = new List<TenantAliasEvidence>();
        foreach (Match match in JwtRegex().Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parts = match.Value.Split('.');
            if (parts.Length < 2)
            {
                continue;
            }

            var payload = DecodeBase64Url(parts[1]);
            if (payload is null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var tid = TryGetString(doc.RootElement, "tid") ?? TryGetString(doc.RootElement, "tenantId");
                if (!string.IsNullOrWhiteSpace(tid))
                {
                    evidence.Add(new TenantAliasEvidence(tid, ExtractAliasFromTokenPayload(doc.RootElement)));
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return evidence;
    }

    private static string? ExtractAliasFromTokenPayload(JsonElement payload)
    {
        foreach (var claimName in new[] { "preferred_username", "upn", "email", "unique_name" })
        {
            var alias = ExtractAliasFromAccountIdentifier(TryGetString(payload, claimName));
            if (!string.IsNullOrWhiteSpace(alias))
            {
                return alias;
            }
        }

        return null;
    }

    private static string? ExtractMicrosoftAccountAliasFromText(string text, CancellationToken cancellationToken)
    {
        foreach (var evidence in ExtractTenantAliasEvidenceFromJwtPayloads(text, cancellationToken))
        {
            if (evidence.TenantId.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(evidence.Alias))
            {
                return evidence.Alias;
            }
        }

        foreach (Match match in MicrosoftAccountRegex().Matches(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var alias = NormalizeAlias(match.Groups["alias"].Value);
            if (!string.IsNullOrWhiteSpace(alias))
            {
                return alias;
            }
        }

        return null;
    }

    private static string? DecodeBase64Url(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
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

    private IEnumerable<string> GetVsCodeStateDatabasePaths()
    {
        var home = _executionContext.HomeDirectory.FullName;

        if (_environment.IsWindows())
        {
            var appData = GetSpecialFolderPath(Environment.SpecialFolder.ApplicationData, "APPDATA");
            if (string.IsNullOrWhiteSpace(appData))
            {
                yield break;
            }

            foreach (var product in GetVsCodeProductNames())
            {
                yield return Path.Combine(appData, product, "User", "globalStorage", "state.vscdb");
            }

            yield break;
        }

        if (_environment.IsMacOS())
        {
            if (string.IsNullOrWhiteSpace(home))
            {
                yield break;
            }

            foreach (var product in GetVsCodeProductNames())
            {
                yield return Path.Combine(home, "Library", "Application Support", product, "User", "globalStorage", "state.vscdb");
            }

            yield break;
        }

        var xdgConfigHome = _environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrWhiteSpace(xdgConfigHome) ? Path.Combine(home, ".config") : xdgConfigHome;
        foreach (var product in GetVsCodeProductNames())
        {
            yield return Path.Combine(configHome, product, "User", "globalStorage", "state.vscdb");
        }

        if (IsWsl())
        {
            yield return Path.Combine(home, ".vscode-server", "data", "User", "globalStorage", "state.vscdb");
            yield return Path.Combine(home, ".vscode-server-insiders", "data", "User", "globalStorage", "state.vscdb");
        }
    }

    private static string[] GetVsCodeProductNames()
    {
        return ["Code", "Code - Insiders", "VSCodium"];
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
        return match.Success ? NormalizeAlias(match.Groups["alias"].Value) : null;
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
        return normalized.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-') ? normalized : null;
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

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._%+\-\\])(?<alias>[A-Za-z0-9._%+-]+)@(?<domain>(?:[A-Za-z0-9-]+\.)*microsoft\.com)(?![A-Za-z0-9._%+-])", RegexOptions.IgnoreCase)]
    private static partial Regex MicrosoftAccountRegex();

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
    private readonly record struct TokenCandidate(string Token);
    private readonly record struct TenantAliasEvidence(string TenantId, string? Alias);
}

internal sealed record InternalMicrosoftProbe(string Name, Func<CancellationToken, Task<InternalMicrosoftProbeResult>> DetectAsync);

internal readonly record struct InternalMicrosoftProbeResult(bool IsInternalMicrosoft, string? Alias, string? Domain)
{
    public static InternalMicrosoftProbeResult NotDetected { get; } = new(IsInternalMicrosoft: false, Alias: null, Domain: null);
}

internal sealed record InternalMicrosoftDetectionResult(bool IsInternalMicrosoft, string? Source, string? Alias, string? Domain);

internal sealed record InternalMicrosoftDetectorCacheEntry
{
    public bool IsInternalMicrosoft { get; init; }
    public string? Source { get; init; }
    public string? Alias { get; init; }
    public string? Domain { get; init; }
    public DateTimeOffset LastRunUtc { get; init; }
}
