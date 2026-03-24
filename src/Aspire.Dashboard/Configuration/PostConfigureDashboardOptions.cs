// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Configuration;

public sealed class PostConfigureDashboardOptions : IPostConfigureOptions<DashboardOptions>
{
    private readonly IConfiguration _configuration;
    private readonly ILogger _logger;

    public PostConfigureDashboardOptions(IConfiguration configuration) : this(configuration, NullLogger<PostConfigureDashboardOptions>.Instance)
    {
    }

    public PostConfigureDashboardOptions(IConfiguration configuration, ILogger<PostConfigureDashboardOptions> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public void PostConfigure(string? name, DashboardOptions options)
    {
        _logger.LogDebug($"PostConfigure {nameof(DashboardOptions)} with name '{name}'.");

        // Copy aliased config values to the strongly typed options.
        if (_configuration.GetString(DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey,
                                     DashboardConfigNames.Legacy.DashboardOtlpGrpcUrlName.ConfigKey, fallbackOnEmpty: true) is { } otlpGrpcUrl)
        {
            options.Otlp.GrpcEndpointUrl = otlpGrpcUrl;
        }

        // Copy aliased config values to the strongly typed options.
        if (_configuration.GetString(DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey,
                                     DashboardConfigNames.Legacy.DashboardOtlpHttpUrlName.ConfigKey, fallbackOnEmpty: true) is { } otlpHttpUrl)
        {
            options.Otlp.HttpEndpointUrl = otlpHttpUrl;
        }

        // Copy aliased config values to the strongly typed options.
        if (_configuration[DashboardConfigNames.DashboardMcpUrlName.ConfigKey] is { Length: > 0 } mcpUrl)
        {
            options.Mcp.EndpointUrl = mcpUrl;
        }

        if (_configuration[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] is { Length: > 0 } frontendUrls)
        {
            options.Frontend.EndpointUrls = frontendUrls;
        }

        if (_configuration.GetString(DashboardConfigNames.ResourceServiceUrlName.ConfigKey,
                                     DashboardConfigNames.Legacy.ResourceServiceUrlName.ConfigKey, fallbackOnEmpty: true) is { } resourceServiceUrl)
        {
            options.ResourceServiceClient.Url = resourceServiceUrl;
        }

        if (_configuration.GetBool(DashboardConfigNames.DashboardUnsecuredAllowAnonymousName.ConfigKey,
                                   DashboardConfigNames.Legacy.DashboardUnsecuredAllowAnonymousName.ConfigKey) ?? false)
        {
            options.Frontend.AuthMode = FrontendAuthMode.Unsecured;
            options.Otlp.AuthMode = OtlpAuthMode.Unsecured;
            options.Mcp.AuthMode = McpAuthMode.Unsecured;
            options.Api.AuthMode = ApiAuthMode.Unsecured;
        }
        else
        {
            options.Frontend.AuthMode ??= FrontendAuthMode.BrowserToken;
            options.Otlp.AuthMode ??= OtlpAuthMode.Unsecured;

            // If an API key is configured, default to ApiKey auth mode instead of Unsecured.
            options.Mcp.AuthMode ??= string.IsNullOrEmpty(options.Mcp.PrimaryApiKey) ? McpAuthMode.Unsecured : McpAuthMode.ApiKey;
            options.Api.AuthMode ??= string.IsNullOrEmpty(options.Api.PrimaryApiKey) ? ApiAuthMode.Unsecured : ApiAuthMode.ApiKey;
        }

        if (options.Frontend.AuthMode == FrontendAuthMode.BrowserToken && string.IsNullOrEmpty(options.Frontend.BrowserToken))
        {
            var token = TokenGenerator.GenerateToken();

            // Set the generated token in configuration. This is required because options could be created multiple times
            // (at startup, after CI is created, after options change). Setting the token in configuration makes it consistent.
            _configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = token;
            options.Frontend.BrowserToken = token;
        }

        options.AI.Disabled = _configuration.GetBool(DashboardConfigNames.DashboardAIDisabledName.ConfigKey);

        options.Api.Enabled ??= _configuration.GetBool(DashboardConfigNames.DashboardAspireApiEnabledName.ConfigKey);

        // Normalize API keys: Api is canonical, falls back to Mcp if not set.
        // Api -> Mcp fallback only (not bidirectional).
        if (string.IsNullOrEmpty(options.Mcp.PrimaryApiKey) && !string.IsNullOrEmpty(options.Api.PrimaryApiKey))
        {
            _logger.LogDebug("Defaulting Mcp.PrimaryApiKey from Api.PrimaryApiKey.");
            options.Mcp.PrimaryApiKey = options.Api.PrimaryApiKey;
        }

        if (string.IsNullOrEmpty(options.Mcp.SecondaryApiKey) && !string.IsNullOrEmpty(options.Api.SecondaryApiKey))
        {
            options.Mcp.SecondaryApiKey = options.Api.SecondaryApiKey;
        }

        if (_configuration.GetBool(DashboardConfigNames.Legacy.DashboardOtlpSuppressUnsecuredTelemetryMessageName.ConfigKey) is { } suppressUnsecuredTelemetryMessage)
        {
            options.Otlp.SuppressUnsecuredMessage = suppressUnsecuredTelemetryMessage;
        }
    }
}
