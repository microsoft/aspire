// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Publishing;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Default implementation of <see cref="IAcrLoginService"/> that handles ACR authentication
/// using Azure credentials and OAuth2 token exchange.
/// </summary>
internal sealed class AcrLoginService : IAcrLoginService
{
    private const string AcrUsername = "00000000-0000-0000-0000-000000000000";
    private const string AcrScope = "https://containerregistry.azure.net/.default";
    // Thirty attempts at a two-second cadence gives new registries about a minute
    // for DNS, data-plane, and RBAC propagation without blocking deployment for too long.
    private const int MaxLoginAttempts = 30;
    private static readonly TimeSpan s_loginRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_maxLoginRetryDuration = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IContainerRuntimeResolver _containerRuntimeResolver;
    private readonly ILogger<AcrLoginService> _logger;
    private readonly TimeProvider _timeProvider;

    private sealed class AcrRefreshTokenResponse
    {
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AcrLoginService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory for making OAuth2 exchange requests.</param>
    /// <param name="containerRuntimeResolver">The container runtime resolver for performing registry login.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <param name="timeProvider">The time provider used for retry delays.</param>
    public AcrLoginService(
        IHttpClientFactory httpClientFactory,
        IContainerRuntimeResolver containerRuntimeResolver,
        ILogger<AcrLoginService> logger,
        TimeProvider timeProvider)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _containerRuntimeResolver = containerRuntimeResolver ?? throw new ArgumentNullException(nameof(containerRuntimeResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc/>
    public async Task LoginAsync(
        string registryEndpoint,
        string tenantId,
        TokenCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(credential);

        // Step 1: Acquire AAD access token for ACR audience
        var tokenRequestContext = new TokenRequestContext([AcrScope]);
        var aadToken = await credential.GetTokenAsync(tokenRequestContext, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("AAD access token acquired for ACR audience, registry: {RegistryEndpoint}, token length: {TokenLength}",
            registryEndpoint, aadToken.Token.Length);

        var containerRuntime = await _containerRuntimeResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var retryStartTimestamp = _timeProvider.GetTimestamp();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Step 2: Exchange AAD token for ACR refresh token
                var refreshToken = await ExchangeAadTokenForAcrRefreshTokenAsync(
                    registryEndpoint, tenantId, aadToken.Token, cancellationToken).ConfigureAwait(false);

                _logger.LogDebug("ACR refresh token acquired, length: {TokenLength}", refreshToken.Length);

                // Step 3: Login to the registry using container runtime
                await containerRuntime.LoginToRegistryAsync(registryEndpoint, AcrUsername, refreshToken, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException ex) when (ShouldRetryAcrLoginFailure(ex, attempt, retryStartTimestamp))
            {
                // New registries can briefly fail DNS resolution or reject token exchange while
                // data-plane endpoint and RBAC propagation catch up with ARM deployment success.
                _logger.LogWarning(
                    ex,
                    "ACR login to {RegistryEndpoint} failed on attempt {Attempt} of {MaxAttempts}. Retrying in {RetryDelay}.",
                    registryEndpoint,
                    attempt,
                    MaxLoginAttempts,
                    s_loginRetryDelay);
                await Task.Delay(s_loginRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private bool ShouldRetryAcrLoginFailure(HttpRequestException ex, int attempt, long retryStartTimestamp)
    {
        return attempt < MaxLoginAttempts &&
            _timeProvider.GetElapsedTime(retryStartTimestamp) < s_maxLoginRetryDuration &&
            IsRetryableAcrLoginFailure(ex);
    }

    private static bool IsRetryableAcrLoginFailure(HttpRequestException ex)
    {
        if (ex.StatusCode is null)
        {
            return true;
        }

        return ex.StatusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.NotFound ||
            (int)ex.StatusCode >= 500;
    }

    private async Task<string> ExchangeAadTokenForAcrRefreshTokenAsync(
        string registryEndpoint,
        string tenantId,
        string aadAccessToken,
        CancellationToken cancellationToken)
    {
        // Use named HTTP client "AcrLogin" which can be configured for debug-level logging
        // via configuration: "Logging": { "LogLevel": { "System.Net.Http.HttpClient.AcrLogin": "Debug" } }
        var httpClient = _httpClientFactory.CreateClient("AcrLogin");
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        // ACR OAuth2 exchange endpoint
        var exchangeUrl = $"https://{registryEndpoint}/oauth2/exchange";

        _logger.LogDebug("Exchanging AAD token for ACR refresh token at {ExchangeUrl} (tenant: {TenantId})",
            exchangeUrl,
            tenantId);

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = "access_token",
            ["service"] = registryEndpoint,
            ["tenant"] = tenantId,
            ["access_token"] = aadAccessToken
        };

        using var content = new FormUrlEncodedContent(formData);
        var response = await httpClient.PostAsync(exchangeUrl, content, cancellationToken).ConfigureAwait(false);

        // Read response body as string once
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var truncatedBody = responseBody.Length <= 1000 ? responseBody : responseBody[..1000] + "…";
            throw new HttpRequestException(
                $"POST /oauth2/exchange failed {(int)response.StatusCode} {response.ReasonPhrase}. Body: {truncatedBody}",
                null,
                response.StatusCode);
        }

        // Deserialize from the string we already read
        var tokenResponse = JsonSerializer.Deserialize<AcrRefreshTokenResponse>(responseBody, s_jsonOptions);

        if (string.IsNullOrEmpty(tokenResponse?.RefreshToken))
        {
            throw new InvalidOperationException($"Response missing refresh_token.");
        }

        return tokenResponse.RefreshToken;
    }
}
