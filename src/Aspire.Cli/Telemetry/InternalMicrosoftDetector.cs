// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static readonly TimeSpan s_cacheRefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan s_processProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_cancelledProbeDrainTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_gitHubHttpTimeout = TimeSpan.FromSeconds(3);

    private readonly string _cacheFilePath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InternalMicrosoftDetector> _logger;
    private readonly IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> _probeStages;

    public InternalMicrosoftDetector(CliExecutionContext executionContext, TimeProvider timeProvider, ILogger<InternalMicrosoftDetector> logger)
        : this(
            Path.Combine(executionContext.CacheDirectory.FullName, CacheSubdirectoryName, CacheFileName),
            timeProvider,
            logger,
            probeStages: null)
    {
    }

    internal InternalMicrosoftDetector(
        string cacheFilePath,
        TimeProvider timeProvider,
        ILogger<InternalMicrosoftDetector> logger,
        IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>>? probeStages)
    {
        _cacheFilePath = cacheFilePath;
        _timeProvider = timeProvider;
        _logger = logger;
        _probeStages = probeStages ?? CreateDefaultProbeStages();
    }

    public async Task<InternalMicrosoftDetectionResult> IsInternalMicrosoftMachineAsync(CancellationToken cancellationToken = default)
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

    private IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> CreateDefaultProbeStages()
    {
        // Fastest/strongest signal probes
        var stage1 = new List<InternalMicrosoftProbe>();
        if (OperatingSystem.IsMacOS())
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

        if (OperatingSystem.IsWindows())
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or HttpRequestException or TaskCanceledException)
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
            TryDelete(tempPath);
        }
    }

    private static Task<InternalMicrosoftProbeResult> CheckWindowsUserDnsDomainAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userDnsDomain = Environment.GetEnvironmentVariable("USERDNSDOMAIN");
        var domain = ExtractAdDomainNameFromCorpDnsName(userDnsDomain);
        return Task.FromResult(domain is not null
            ? Detected(Environment.GetEnvironmentVariable("USERNAME"), domain)
            : InternalMicrosoftProbeResult.NotDetected);
    }

    private async Task<InternalMicrosoftProbeResult> CheckWslWindowsUserDnsDomainAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("cmd.exe", "/c \"echo %USERDNSDOMAIN%&echo %USERNAME%\"", cancellationToken).ConfigureAwait(false);
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
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var accountStore = Path.Combine(localAppData, ".IdentityService", "V3AccountStore.json");
        if (!File.Exists(accountStore))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var text = await TryReadAllTextAsync(accountStore, cancellationToken).ConfigureAwait(false);
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
            "/c if exist \"%LOCALAPPDATA%\\.IdentityService\\V3AccountStore.json\" type \"%LOCALAPPDATA%\\.IdentityService\\V3AccountStore.json\"",
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

        var result = await RunProcessAsync("app-sso", "platform -s", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var output = $"{result.Stdout}{Environment.NewLine}{result.Stderr}";
        var expectedIssuer = $"https://login.microsoftonline.com/{MicrosoftTenantId}/v2.0";
        var expectedKeyEndpoint = $"https://login.microsoftonline.com/{MicrosoftTenantId}/getkeydata";
        var expectedTokenEndpoint = $"https://login.microsoftonline.com/{MicrosoftTenantId}/oauth2/v2.0/token";

        var upnMatch = PlatformSsoUpnRegex().Match(output);
        return ContainsJsonStringProperty(output, "issuer", expectedIssuer) &&
            ContainsJsonStringProperty(output, "keyEndpointURL", expectedKeyEndpoint) &&
            ContainsJsonStringProperty(output, "tokenEndpointURL", expectedTokenEndpoint) &&
            PlatformSsoRealmRegex().IsMatch(output) &&
            upnMatch.Success
                ? Detected(upnMatch.Groups["username"].Value, upnMatch.Groups["domain"].Value)
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

            var bytes = await TryReadAllBytesAsync(stateDatabasePath, cancellationToken).ConfigureAwait(false);
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

    private async Task<InternalMicrosoftProbeResult> CheckWindowsWorkplaceJoinAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("dsregcmd"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("dsregcmd", "/status", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? EvaluateWindowsWorkplaceJoin(
                result.Stdout,
                Environment.GetEnvironmentVariable("USERNAME"),
                Environment.GetEnvironmentVariable("USERDNSDOMAIN"))
            : InternalMicrosoftProbeResult.NotDetected;
    }

    private async Task<InternalMicrosoftProbeResult> CheckWslWindowsWorkplaceJoinAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var result = await RunProcessAsync("cmd.exe", "/c dsregcmd /status", cancellationToken).ConfigureAwait(false);
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

        var tokenResult = await RunProcessAsync("gh", "auth token --hostname github.com", cancellationToken).ConfigureAwait(false);
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

        var tokenResult = await RunProcessAsync("gh.exe", "auth token --hostname github.com", cancellationToken).ConfigureAwait(false);
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

        using var http = CreateGitHubHttpClient();
        return await CheckAnyGitHubMembershipCandidateAsync(http, tokenCandidates, cancellationToken).ConfigureAwait(false)
            ? Detected(alias: null)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    private async Task<InternalMicrosoftProbeResult> CheckCopilotCliAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("copilot"))
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        var tokenCandidates = new List<TokenCandidate>();
        foreach (var (name, value) in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Name: e.Key?.ToString() ?? string.Empty, Value: e.Value?.ToString() ?? string.Empty)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (name.StartsWith("COPILOT_GH_ACCOUNT_", StringComparison.OrdinalIgnoreCase) && LooksLikeGitHubToken(value))
            {
                tokenCandidates.Add(new TokenCandidate(value));
            }
        }

        var copilotHome = Path.Combine(GetHomeDirectory(), ".copilot");
        foreach (var path in EnumerateExistingFiles(copilotHome, cancellationToken, "config.json", "settings.json"))
        {
            tokenCandidates.AddRange(ExtractGitHubTokenCandidates(path, cancellationToken));
        }

        tokenCandidates = DeduplicateTokenCandidates(tokenCandidates);
        if (tokenCandidates.Count == 0)
        {
            return InternalMicrosoftProbeResult.NotDetected;
        }

        using var http = CreateGitHubHttpClient();
        return await CheckAnyGitHubMembershipCandidateAsync(http, tokenCandidates, cancellationToken).ConfigureAwait(false)
            ? Detected(alias: null)
            : InternalMicrosoftProbeResult.NotDetected;
    }

    private static async Task<bool> CheckAnyGitHubMembershipCandidateAsync(HttpClient http, IReadOnlyList<TokenCandidate> candidates, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await CheckGitHubMembershipWithTokenAsync(http, candidate.Token, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
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

        using var publicMemberRequest = NewGitHubRequest(HttpMethod.Get, $"https://api.github.com/orgs/{MicrosoftGitHubOrg}/members/{login}", token);
        using var publicMemberResponse = await http.SendAsync(publicMemberRequest, cancellationToken).ConfigureAwait(false);
        return publicMemberResponse.StatusCode == HttpStatusCode.NoContent;
    }

    private static HttpClient CreateGitHubHttpClient()
    {
        var http = new HttpClient
        {
            Timeout = s_gitHubHttpTimeout
        };

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

    private async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var result = await ProcessCaptureRunner.RunAsync(
            startInfo,
            s_processProbeTimeout,
            async (process, captureCancellationToken) =>
            {
                // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
                var stdoutTask = process.StandardOutput.ReadToEndAsync(captureCancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(captureCancellationToken);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return new ProcessOutput(stdoutTask.Result, stderrTask.Result);
            },
            static () => new ProcessOutput(string.Empty, string.Empty),
            _logger,
            cancellationToken).ConfigureAwait(false);

        return new ProcessResult(result.ExitCode, result.Capture.Stdout, result.Capture.Stderr);
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
    }

    private static Dictionary<string, string> ParseColonSeparatedFields(string text)
    {
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

    private static IEnumerable<TokenCandidate> GetGitHubTokenEnvironmentCandidates(CancellationToken cancellationToken)
    {
        var exactNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GH_TOKEN",
            "GITHUB_TOKEN",
            "GITHUB_PAT",
            "GITHUB_OAUTH_TOKEN",
            "GITHUB_ACCESS_TOKEN"
        };

        foreach (var (name, value) in Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Name: e.Key?.ToString() ?? string.Empty, Value: e.Value?.ToString() ?? string.Empty)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (exactNames.Contains(name) && LooksLikeGitHubToken(value))
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

    private static IEnumerable<string> GetVsCodeStateDatabasePaths()
    {
        var home = GetHomeDirectory();

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
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

        if (OperatingSystem.IsMacOS())
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

        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
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

    private static bool IsWsl()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP")))
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

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extensions = OperatingSystem.IsWindows() && string.IsNullOrEmpty(Path.GetExtension(command))
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
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

    private static async Task<string> TryReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static async Task<byte[]> TryReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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

    private static string? NormalizeAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        var normalized = alias.Trim();
        return normalized.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-') ? normalized : null;
    }

    private static bool ContainsJsonStringProperty(string text, string propertyName, string expectedValue)
    {
        return Regex.IsMatch(
            text,
            $@"""{Regex.Escape(propertyName)}""\s*:\s*""{Regex.Escape(expectedValue)}""",
            RegexOptions.IgnoreCase);
    }

    private static bool IsYes(string? value)
    {
        return value?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool LooksLikeGitHubToken(string token)
    {
        return GitHubTokenRegex().IsMatch(token);
    }

    private static string GetHomeDirectory()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"(?:github_pat_[A-Za-z0-9_]{20,}|gh[opsru]_[A-Za-z0-9_]{20,})")]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._%+\-\\])(?<alias>[A-Za-z0-9._%+-]+)@(?<domain>(?:[A-Za-z0-9-]+\.)*microsoft\.com)(?![A-Za-z0-9._%+-])", RegexOptions.IgnoreCase)]
    private static partial Regex MicrosoftAccountRegex();

    [GeneratedRegex(@"""realm""\s*:\s*""[A-Z0-9.-]+\.CORP\.MICROSOFT\.COM""", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformSsoRealmRegex();

    [GeneratedRegex(@"""upn""\s*:\s*""(?<username>[^""@\s]+)@(?<domain>[A-Z0-9.-]+\.CORP\.MICROSOFT\.COM)""", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformSsoUpnRegex();

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
    private readonly record struct ProcessOutput(string Stdout, string Stderr);
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
