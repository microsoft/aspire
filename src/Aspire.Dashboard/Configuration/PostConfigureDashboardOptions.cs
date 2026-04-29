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
            options.Api.AuthMode = ApiAuthMode.Unsecured;
        }
        else
        {
            options.Frontend.AuthMode ??= FrontendAuthMode.BrowserToken;
            options.Otlp.AuthMode ??= OtlpAuthMode.Unsecured;
            options.Api.AuthMode ??= ApiAuthMode.ApiKey;
        }

        if (options.Frontend.AuthMode == FrontendAuthMode.BrowserToken && string.IsNullOrEmpty(options.Frontend.BrowserToken))
        {
            var token = TokenGenerator.GenerateToken();

            // Set the generated token in configuration. This is required because options could be created multiple times
            // (at startup, after CI is created, after options change). Setting the token in configuration makes it consistent.
            _configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = token;
            options.Frontend.BrowserToken = token;
        }

        if (options.Api.AuthMode == ApiAuthMode.ApiKey && string.IsNullOrEmpty(options.Api.PrimaryApiKey))
        {
            var apiKey = TokenGenerator.GenerateToken();

            // Set the generated API key in configuration. This is required because options could be created multiple times
            // (at startup, after CI is created, after options change). Setting the key in configuration makes it consistent.
            _configuration[DashboardConfigNames.DashboardApiPrimaryApiKeyName.ConfigKey] = apiKey;
            options.Api.PrimaryApiKey = apiKey;
        }

        // ASPIRE_DASHBOARD_AI_DISABLED takes precendence over ASPIRE__DASHBOARD__AI__DISABLED.
        if (_configuration.GetBool(DashboardConfigNames.DashboardAIDisabledName.ConfigKey) is { } aiDisabled)
        {
            options.AI.Disabled = aiDisabled;
        }
        else
        {
            // If there is no explicit setting then default to disabled.
            options.AI.Disabled = true;
        }

        // DashboardAspireApiDisabledName takes precedence over DashboardAspireApiEnabledName.
        if (_configuration.GetBool(DashboardConfigNames.DashboardAspireApiDisabledName.ConfigKey) is { } apiDisabled)
        {
            options.Api.Disabled ??= apiDisabled;
        }
        else if (_configuration.GetBool(DashboardConfigNames.DashboardAspireApiEnabledName.ConfigKey) is { } apiEnabled)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            options.Api.Enabled ??= apiEnabled;
#pragma warning restore CS0618
        }

        options.Api.Disabled ??= false;

        if (_configuration.GetBool(DashboardConfigNames.Legacy.DashboardOtlpSuppressUnsecuredTelemetryMessageName.ConfigKey) is { } suppressUnsecuredTelemetryMessage)
        {
            options.Otlp.SuppressUnsecuredMessage = suppressUnsecuredTelemetryMessage;
        }
    }
}
