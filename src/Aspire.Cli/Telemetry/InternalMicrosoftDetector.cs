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
    Task<bool> IsInternalMicrosoftMachineAsync(CancellationToken cancellationToken = default);
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

    public async Task<bool> IsInternalMicrosoftMachineAsync(CancellationToken cancellationToken = default)
    {
        var cached = await TryReadFreshCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.IsInternalMicrosoft;
        }

        var isInternalMicrosoft = await RunProbeStagesAsync(cancellationToken).ConfigureAwait(false);
        await TryWriteCacheAsync(isInternalMicrosoft, cancellationToken).ConfigureAwait(false);

        return isInternalMicrosoft;
    }

    private IReadOnlyList<IReadOnlyList<InternalMicrosoftProbe>> CreateDefaultProbeStages()
    {
        var stage1 = new List<InternalMicrosoftProbe>();
        var stage2 = new List<InternalMicrosoftProbe>
        {
            new("VS Code Microsoft tenant", CheckVsCodeMicrosoftTenantAsync)
        };
        var stage3 = new List<InternalMicrosoftProbe>
        {
            new("Environment GitHub token membership", CheckEnvironmentGitHubTokenAsync),
            new("gh CLI GitHub org membership", CheckGhCliAsync),
            new("Copilot CLI GitHub org membership", CheckCopilotCliAsync)
        };

        if (OperatingSystem.IsWindows())
        {
            stage1.Add(new("Windows USERDNSDOMAIN", CheckWindowsUserDnsDomainAsync));
            stage1.Add(new("Visual Studio Microsoft tenant", CheckVisualStudioMicrosoftTenantAsync));
            stage3.Add(new("Windows workplace join", CheckWindowsWorkplaceJoinAsync));
        }
        else if (IsWsl())
        {
            stage1.Add(new("WSL Windows USERDNSDOMAIN", CheckWslWindowsUserDnsDomainAsync));
            stage1.Add(new("WSL Visual Studio Microsoft tenant", CheckWslVisualStudioMicrosoftTenantAsync));
            stage3.Add(new("WSL Windows workplace join", CheckWslWindowsWorkplaceJoinAsync));
            stage3.Add(new("WSL Windows gh.exe GitHub org membership", CheckWslWindowsGhCliAsync));
        }

        if (OperatingSystem.IsMacOS())
        {
            stage1.Add(new("Mac Platform SSO", CheckMacPlatformSsoAsync));
        }

        return [stage1, stage2, stage3];
    }

    private async Task<bool> RunProbeStagesAsync(CancellationToken cancellationToken)
    {
        foreach (var stage in _probeStages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (stage.Count == 0)
            {
                continue;
            }

            if (await RunProbeStageAsync(stage, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> RunProbeStageAsync(IReadOnlyList<InternalMicrosoftProbe> probes, CancellationToken cancellationToken)
    {
        using var stageCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeTasks = probes.Select(probe => RunProbeAsync(probe, stageCancellation.Token)).ToList();
        var pendingTasks = probeTasks.ToList();

        while (pendingTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingTasks).ConfigureAwait(false);
            pendingTasks.Remove(completedTask);

            if (await completedTask.ConfigureAwait(false))
            {
                await stageCancellation.CancelAsync().ConfigureAwait(false);
                await DrainCancelledProbesAsync(probeTasks).ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    private Task<bool> RunProbeAsync(InternalMicrosoftProbe probe, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await probe.DetectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or HttpRequestException or TaskCanceledException)
            {
                _logger.LogDebug(ex, "Microsoft internal probe '{ProbeName}' failed.", probe.Name);
                return false;
            }
        }, CancellationToken.None);
    }

    private async Task DrainCancelledProbesAsync(IReadOnlyList<Task<bool>> probeTasks)
    {
        try
        {
            await Task.WhenAll(probeTasks).WaitAsync(s_cancelledProbeDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogDebug(ex, "Timed out waiting for cancelled Microsoft internal probes to drain.");
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

            return _timeProvider.GetUtcNow() - entry.LastRunUtc < s_cacheRefreshInterval
                ? entry
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Failed to read Microsoft internal detector cache from {CacheFilePath}.", _cacheFilePath);
            return null;
        }
    }

    private async Task TryWriteCacheAsync(bool isInternalMicrosoft, CancellationToken cancellationToken)
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
                IsInternalMicrosoft = isInternalMicrosoft,
                LastRunUtc = _timeProvider.GetUtcNow()
            };
            var json = JsonSerializer.Serialize(entry, JsonSourceGenerationContext.Default.InternalMicrosoftDetectorCacheEntry);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, _cacheFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to write Microsoft internal detector cache to {CacheFilePath}.", _cacheFilePath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static Task<bool> CheckWindowsUserDnsDomainAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsCorpMicrosoftDomain(Environment.GetEnvironmentVariable("USERDNSDOMAIN")));
    }

    private async Task<bool> CheckWslWindowsUserDnsDomainAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return false;
        }

        var result = await RunProcessAsync("cmd.exe", "/c echo %USERDNSDOMAIN%", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && IsCorpMicrosoftDomain(result.Stdout.Trim());
    }

    [SupportedOSPlatform("windows")]
    private async Task<bool> CheckVisualStudioMicrosoftTenantAsync(CancellationToken cancellationToken)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return false;
        }

        var accountStore = Path.Combine(localAppData, ".IdentityService", "V3AccountStore.json");
        if (!File.Exists(accountStore))
        {
            return false;
        }

        var text = await TryReadAllTextAsync(accountStore, cancellationToken).ConfigureAwait(false);
        return ContainsMicrosoftTenant(text, cancellationToken);
    }

    private async Task<bool> CheckWslVisualStudioMicrosoftTenantAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return false;
        }

        var result = await RunProcessAsync(
            "cmd.exe",
            "/c if exist \"%LOCALAPPDATA%\\.IdentityService\\V3AccountStore.json\" type \"%LOCALAPPDATA%\\.IdentityService\\V3AccountStore.json\"",
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode == 0 && ContainsMicrosoftTenant(result.Stdout, cancellationToken);
    }

    [SupportedOSPlatform("macos")]
    private async Task<bool> CheckMacPlatformSsoAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("app-sso"))
        {
            return false;
        }

        var result = await RunProcessAsync("app-sso", "platform -s", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return false;
        }

        var output = $"{result.Stdout}{Environment.NewLine}{result.Stderr}";
        var expectedIssuer = $"https://login.microsoftonline.com/{MicrosoftTenantId}/v2.0";
        var expectedKeyEndpoint = $"https://login.microsoftonline.com/{MicrosoftTenantId}/getkeydata";
        var expectedTokenEndpoint = $"https://login.microsoftonline.com/{MicrosoftTenantId}/oauth2/v2.0/token";

        return ContainsJsonStringProperty(output, "issuer", expectedIssuer) &&
            ContainsJsonStringProperty(output, "keyEndpointURL", expectedKeyEndpoint) &&
            ContainsJsonStringProperty(output, "tokenEndpointURL", expectedTokenEndpoint) &&
            PlatformSsoRealmRegex().IsMatch(output) &&
            PlatformSsoUpnRegex().IsMatch(output);
    }

    private async Task<bool> CheckVsCodeMicrosoftTenantAsync(CancellationToken cancellationToken)
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
            if (ContainsMicrosoftTenant(text, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> CheckWindowsWorkplaceJoinAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("dsregcmd"))
        {
            return false;
        }

        var result = await RunProcessAsync("dsregcmd", "/status", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && EvaluateWindowsWorkplaceJoin(result.Stdout);
    }

    private async Task<bool> CheckWslWindowsWorkplaceJoinAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("cmd.exe"))
        {
            return false;
        }

        var result = await RunProcessAsync("cmd.exe", "/c dsregcmd /status", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && EvaluateWindowsWorkplaceJoin(result.Stdout);
    }

    private async Task<bool> CheckGhCliAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("gh"))
        {
            return false;
        }

        var tokenResult = await RunProcessAsync("gh", "auth token --hostname github.com", cancellationToken).ConfigureAwait(false);
        if (tokenResult.ExitCode != 0 || string.IsNullOrWhiteSpace(tokenResult.Stdout))
        {
            return false;
        }

        using var http = CreateGitHubHttpClient();
        return await CheckGitHubMembershipWithTokenAsync(http, tokenResult.Stdout.Trim(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CheckWslWindowsGhCliAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("gh.exe"))
        {
            return false;
        }

        var tokenResult = await RunProcessAsync("gh.exe", "auth token --hostname github.com", cancellationToken).ConfigureAwait(false);
        if (tokenResult.ExitCode != 0 || string.IsNullOrWhiteSpace(tokenResult.Stdout))
        {
            return false;
        }

        using var http = CreateGitHubHttpClient();
        return await CheckGitHubMembershipWithTokenAsync(http, tokenResult.Stdout.Trim(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CheckEnvironmentGitHubTokenAsync(CancellationToken cancellationToken)
    {
        var tokenCandidates = DeduplicateTokenCandidates(GetGitHubTokenEnvironmentCandidates(cancellationToken));
        if (tokenCandidates.Count == 0)
        {
            return false;
        }

        using var http = CreateGitHubHttpClient();
        return await CheckAnyGitHubMembershipCandidateAsync(http, tokenCandidates, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CheckCopilotCliAsync(CancellationToken cancellationToken)
    {
        if (!CommandExists("copilot"))
        {
            return false;
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
            return false;
        }

        using var http = CreateGitHubHttpClient();
        return await CheckAnyGitHubMembershipCandidateAsync(http, tokenCandidates, cancellationToken).ConfigureAwait(false);
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

    private static bool EvaluateWindowsWorkplaceJoin(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var values = ParseColonSeparatedFields(output);
        var tenantId = values.GetValueOrDefault("TenantId");
        var azureAdJoined = IsYes(values.GetValueOrDefault("AzureAdJoined"));
        var workplaceJoined = IsYes(values.GetValueOrDefault("WorkplaceJoined"));

        return (azureAdJoined || workplaceJoined) && tenantId?.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase) == true;
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

    private static bool ContainsMicrosoftTenant(string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var tenantId in ExtractTenantIdsFromJwtPayloads(text, cancellationToken))
        {
            if (tenantId.Equals(MicrosoftTenantId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ExtractTenantIdsFromJwtPayloads(string text, CancellationToken cancellationToken)
    {
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

            string? tid;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                tid = TryGetString(doc.RootElement, "tid") ?? TryGetString(doc.RootElement, "tenantId");
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(tid))
            {
                yield return tid;
            }
        }
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

        var text = TryReadAllTextAsync(filePath, cancellationToken).GetAwaiter().GetResult();
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

    private static bool IsCorpMicrosoftDomain(string? value)
    {
        return value?.EndsWith(CorpMicrosoftDomainSuffix, StringComparison.OrdinalIgnoreCase) == true;
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

    [GeneratedRegex(@"(?:github_pat_[A-Za-z0-9_]{20,}|gh[opsru]_[A-Za-z0-9_]{20,})", RegexOptions.Compiled)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.Compiled)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"""realm""\s*:\s*""[A-Z0-9.-]+\.CORP\.MICROSOFT\.COM""", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformSsoRealmRegex();

    [GeneratedRegex(@"""upn""\s*:\s*""[^""@\s]+@[A-Z0-9.-]+\.CORP\.MICROSOFT\.COM""", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformSsoUpnRegex();

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
    private readonly record struct ProcessOutput(string Stdout, string Stderr);
    private readonly record struct TokenCandidate(string Token);
}

internal sealed record InternalMicrosoftProbe(string Name, Func<CancellationToken, Task<bool>> DetectAsync);

internal sealed record InternalMicrosoftDetectorCacheEntry
{
    public bool IsInternalMicrosoft { get; init; }
    public DateTimeOffset LastRunUtc { get; init; }
}
