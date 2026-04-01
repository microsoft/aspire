// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Lightweight GitHub API client that uses the <c>gh</c> CLI for authentication
/// and <see cref="HttpClient"/> for REST API calls.
/// </summary>
internal sealed class GitHubApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public GitHubApiClient(ILogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com"),
            DefaultRequestHeaders =
            {
                { "Accept", "application/vnd.github+json" },
                { "User-Agent", "aspire-pipeline-init" },
                { "X-GitHub-Api-Version", "2022-11-28" }
            }
        };
    }

    /// <summary>
    /// Checks whether the <c>gh</c> CLI is installed and available on PATH.
    /// </summary>
    public static async Task<bool> IsGhInstalledAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, _) = await RunGhAsync("--version", ct).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets an authentication token from the <c>gh</c> CLI.
    /// Returns <c>null</c> if the user is not authenticated.
    /// </summary>
    public async Task<string?> GetAuthTokenAsync(CancellationToken ct = default)
    {
        var (exitCode, output) = await RunGhAsync("auth token", ct).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogDebug("gh auth token returned exit code {ExitCode}. User may not be authenticated.", exitCode);
            return null;
        }

        var token = output.Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>
    /// Gets the authenticated user's login name.
    /// </summary>
    public async Task<string?> GetAuthenticatedUserAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("GET /user returned {StatusCode}", response.StatusCode);
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }

    /// <summary>
    /// Gets the list of organizations the authenticated user is a member of.
    /// Returns org login names.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetUserOrgsAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/user/orgs?per_page=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("GET /user/orgs returned {StatusCode}", response.StatusCode);
            return [];
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);

        var orgs = new List<string>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("login", out var login) && login.GetString() is { } orgLogin)
            {
                orgs.Add(orgLogin);
            }
        }

        return orgs;
    }

    /// <summary>
    /// Creates a new GitHub repository.
    /// </summary>
    /// <param name="token">GitHub auth token.</param>
    /// <param name="name">Repository name.</param>
    /// <param name="org">Organization login, or <c>null</c> for a personal repo.</param>
    /// <param name="isPrivate">Whether the repo should be private.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full clone URL (https), or <c>null</c> on failure.</returns>
    public async Task<string?> CreateRepoAsync(string token, string name, string? org, bool isPrivate, CancellationToken ct = default)
    {
        var endpoint = org is not null ? $"/orgs/{org}/repos" : "/user/repos";

        var body = JsonSerializer.Serialize(new
        {
            name,
            @private = isPrivate,
            auto_init = false
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("Failed to create repository. Status: {StatusCode}, Body: {Body}", response.StatusCode, errorBody);
            return null;
        }

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("clone_url", out var cloneUrl) ? cloneUrl.GetString() : null;
    }

    private static async Task<(int ExitCode, string Output)> RunGhAsync(string arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("gh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return (process.ExitCode, output);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
