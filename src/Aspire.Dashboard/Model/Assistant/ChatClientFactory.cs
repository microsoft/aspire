// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model.Assistant.Ghcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Model.Assistant;

public sealed class ChatClientFactory
{
#if DEBUG
    private const string OpenAIOptInName = "ASPIRE_AI_OPENAI_OPT_IN";
    private const string OpenAIEndpointName = "ASPIRE_AI_ENDPOINT";
    private const string OpenAIApiKeyName = "ASPIRE_AI_APIKEY";
#endif

    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptionsMonitor<DashboardOptions> _dashboardOptions;
    private readonly ILogger<ChatClientFactory> _logger;
    private readonly object _lock = new();

    private Uri? _endpoint;
    private string? _credential;
    private HttpClient? _httpClient;
    private bool _initialized;
    private bool? _enabled;
    private DashboardOptions Options => _dashboardOptions.CurrentValue;

    public ChatClientFactory(IConfiguration configuration, ILoggerFactory loggerFactory, IOptionsMonitor<DashboardOptions> dashboardOptions)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _dashboardOptions = dashboardOptions;
        _logger = loggerFactory.CreateLogger<ChatClientFactory>();
    }

    public IChatClient CreateClient(string model)
    {
        EnsureInitialized();

        var innerChatClient = new OpenAI.Chat.ChatClient(
            model.ToLower(CultureInfo.InvariantCulture),
            new ApiKeyCredential(_credential),
            new()
            {
                Endpoint = _endpoint,
                Transport = new HttpClientPipelineTransport(_httpClient)
            }).AsIChatClient();

        return new ChatClientBuilder(innerChatClient)
            .UseFunctionInvocation(_loggerFactory, options => options.AllowConcurrentInvocation = true)
            .UseLogging(_loggerFactory)
            .Build();
    }

    [MemberNotNull(nameof(_endpoint), nameof(_credential), nameof(_httpClient))]
    private void EnsureInitialized()
    {
        try
        {
            if (_initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                // Configuration won't change for the lifetime of the dashboard.
                // Also, reuse the same HttpClient to avoid unnecessarily creating new connections.
                InitializeClientConfiguration(out _endpoint, out _credential, out _httpClient);
                _initialized = true;

                _logger.LogInformation("Chat client factory initialized. Endpoint = {Endpoint}", _endpoint);
            }
        }
        finally
        {
            Debug.Assert(_endpoint is not null && _credential is not null && _httpClient is not null);
        }
    }

    public async Task<GhcpInfoResponse> GetInfoAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();

#if DEBUG
        if (UseOpenAI())
        {
            return new GhcpInfoResponse
            {
                State = GhcpState.Enabled,
                Models = new List<GhcpModelResponse>
                {
                    new()
                    {
                        Name = "gpt-4o",
                        Family = "gpt-4o",
                        DisplayName = "GPT-4o"
                    },
                    new()
                    {
                        Name = "gpt-4o-mini",
                        Family = "gpt-4o-mini",
                        DisplayName = "GPT-4o-mini"
                    },
                    new()
                    {
                        Name = "o3-mini",
                        Family = "o3-mini",
                        DisplayName = "o3-mini"
                    }

                }
            };
        }
#endif

        try
        {
            var ghcpInfoAddress = new Uri(_endpoint, "/ghcp_info");

            _logger.LogInformation("Requesting GHCP info from {Endpoint}.", ghcpInfoAddress);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, ghcpInfoAddress);
            httpRequest.Headers.Add("Authorization", "Bearer " + _credential);

            using var httpResponse = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            httpResponse.EnsureSuccessStatusCode();

            var response = await httpResponse.Content.ReadFromJsonAsync<GhcpInfoResponse>(cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                throw new InvalidOperationException("Unexpected response from GHCP.");
            }

            _logger.LogInformation("Received GHCP info. State = {State}, Models = {Models}", response.State, string.Join(", ", response.Models?.Select(m => m.Name) ?? []));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting GHCP info.");

            throw new InvalidOperationException("Error response from GHCP.", ex);
        }
    }

    public bool IsEnabled()
    {
        if (_enabled is null)
        {
            // Cache value.
            _enabled = IsEnabledCore();
        }

        return _enabled.Value;

        bool IsEnabledCore()
        {
#if DEBUG
            // Check if the AI is enabled via OpenAI configuration.
            // An debug session connection isn't needed in this case.
            if (UseOpenAI())
            {
                _logger.LogDebug("AI is enabled via OpenAI.");
                return true;
            }
#endif

            // No debug session configured. Expected when the app host is started from CLI.
            if (!DebugSessionHelpers.HasDebugSession(_dashboardOptions.CurrentValue.DebugSession, out _, out _, out _))
            {
                _logger.LogDebug("AI is disabled because there isn't a debug session.");
                return false;
            }

            return true;
        }
    }

    private void InitializeClientConfiguration(out Uri endpoint, out string credential, out HttpClient httpClient)
    {
#if DEBUG
        if (UseOpenAI())
        {
            var url = _configuration[OpenAIEndpointName] is { Length: > 0 } endpointValue
                ? endpointValue
                : "https://models.inference.ai.azure.com";

            endpoint = new Uri(url);

            if (_configuration[OpenAIApiKeyName] is { Length: > 0 } apiKeyValue)
            {
                credential = apiKeyValue;
            }
            else
            {
                throw new InvalidOperationException($"{OpenAIApiKeyName} is not set");
            }

            var handler = new HttpClientHandler();
            httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            return;
        }
#endif

        if (!DebugSessionHelpers.HasDebugSession(Options.DebugSession, out var serverCert, out var debugSessionUri, out var token))
        {
            throw new InvalidOperationException("Debug session port is not set.");
        }

        credential = token;

        var uriBuilder = new UriBuilder(debugSessionUri);
        uriBuilder.Path = "/v1";
        endpoint = uriBuilder.Uri;

        // Don't pass the URL or token here because they're added to the request headers by OpenAI client.
        httpClient = DebugSessionHelpers.CreateHttpClient(debugSessionUri: null, token: null, cert: serverCert, createHandler: null);
    }

#if DEBUG
    private bool UseOpenAI()
    {
        return _configuration.GetBool(OpenAIOptInName) ?? false;
    }
#endif
}
