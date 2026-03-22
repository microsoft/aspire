// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Configuration;

public sealed class ValidateDashboardOptions : IValidateOptions<DashboardOptions>
{
    private readonly ILogger _logger;

    public ValidateDashboardOptions() : this(NullLogger<ValidateDashboardOptions>.Instance)
    {
    }

    public ValidateDashboardOptions(ILogger<ValidateDashboardOptions> logger)
    {
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, DashboardOptions options)
    {
        _logger.LogDebug($"Validating {nameof(DashboardOptions)}.");

        var errorMessages = new List<string>();

        if (!options.Frontend.TryParseOptions(out var frontendParseErrorMessage))
        {
            errorMessages.Add(frontendParseErrorMessage);
        }

        switch (options.Frontend.AuthMode)
        {
            case FrontendAuthMode.Unsecured:
                break;
            case FrontendAuthMode.OpenIdConnect:
                if (!options.Frontend.OpenIdConnect.TryParseOptions(out var messages))
                {
                    errorMessages.AddRange(messages);
                }
                break;
            case FrontendAuthMode.BrowserToken:
                if (string.IsNullOrEmpty(options.Frontend.BrowserToken))
                {
                    errorMessages.Add($"BrowserToken is required when frontend authentication mode is browser token. Specify a {DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey} value.");
                }
                break;
            case null:
                errorMessages.Add($"Frontend endpoint authentication is not configured. Either specify {DashboardConfigNames.DashboardUnsecuredAllowAnonymousName.ConfigKey}=true, or specify {DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey}. Possible values: {string.Join(", ", typeof(FrontendAuthMode).GetEnumNames())}");
                break;
            default:
                errorMessages.Add($"Unexpected frontend authentication mode: {options.Otlp.AuthMode}");
                break;
        }

        if (options.Frontend.MaxConsoleLogCount <= 0)
        {
            errorMessages.Add($"{DashboardConfigNames.DashboardFrontendMaxConsoleLogCountName.ConfigKey} must be greater than zero.");
        }

        if (!options.Otlp.TryParseOptions(out var otlpParseErrorMessage))
        {
            errorMessages.Add(otlpParseErrorMessage);
        }

        // Only validate OTLP auth mode if at least one OTLP endpoint is configured
        var hasOtlpEndpoint = !string.IsNullOrEmpty(options.Otlp.GrpcEndpointUrl) || !string.IsNullOrEmpty(options.Otlp.HttpEndpointUrl);
        if (hasOtlpEndpoint)
        {
            switch (options.Otlp.AuthMode)
            {
                case OtlpAuthMode.Unsecured:
                    break;
                case OtlpAuthMode.ApiKey:
                    if (string.IsNullOrEmpty(options.Otlp.PrimaryApiKey))
                    {
                        errorMessages.Add($"PrimaryApiKey is required when OTLP authentication mode is API key. Specify a {DashboardConfigNames.DashboardOtlpPrimaryApiKeyName.ConfigKey} value.");
                    }
                    break;
                case OtlpAuthMode.ClientCertificate:
                    for (var i = 0; i < options.Otlp.AllowedCertificates.Count; i++)
                    {
                        var allowedCertRule = options.Otlp.AllowedCertificates[i];
                        if (string.IsNullOrEmpty(allowedCertRule.Thumbprint))
                        {
                            errorMessages.Add($"Thumbprint on allow certificate rule is not configured. Specify a {DashboardConfigNames.DashboardOtlpAllowedCertificatesName.ConfigKey}:{i}:Thumbprint value.");
                        }
                    }
                    break;
                case null:
                    errorMessages.Add($"OTLP endpoint authentication is not configured. Either specify {DashboardConfigNames.DashboardUnsecuredAllowAnonymousName.ConfigKey}=true, or specify {DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey}. Possible values: {string.Join(", ", typeof(OtlpAuthMode).GetEnumNames())}");
                    break;
                default:
                    errorMessages.Add($"Unexpected OTLP authentication mode: {options.Otlp.AuthMode}");
                    break;
            }
        }

        if (!options.Mcp.TryParseOptions(out var mcpParseErrorMessage))
        {
            errorMessages.Add(mcpParseErrorMessage);
        }

        // Parse Dashboard API options
        if (!options.Api.TryParseOptions(out var apiParseErrorMessage))
        {
            errorMessages.Add(apiParseErrorMessage);
        }

        // Validate API auth configuration
        var effectiveApiAuthMode = options.Api.AuthMode;
        // Also check MCP-specific AuthMode for backward compatibility
        if (effectiveApiAuthMode is null && options.Mcp.AuthMode is not null)
        {
            effectiveApiAuthMode = options.Mcp.AuthMode switch
            {
                McpAuthMode.ApiKey => ApiAuthMode.ApiKey,
                McpAuthMode.Unsecured => ApiAuthMode.Unsecured,
                _ => null
            };
        }

        if (effectiveApiAuthMode is ApiAuthMode.ApiKey)
        {
            // Check if any API key is configured (either in Api or Mcp section)
            var hasApiKey = !string.IsNullOrEmpty(options.Api.PrimaryApiKey) || !string.IsNullOrEmpty(options.Mcp.PrimaryApiKey);
            if (!hasApiKey)
            {
                errorMessages.Add($"PrimaryApiKey is required when API authentication mode is ApiKey. Specify Dashboard:Api:PrimaryApiKey or Dashboard:Mcp:PrimaryApiKey.");
            }
        }

        if (!options.ResourceServiceClient.TryParseOptions(out var resourceServiceClientParseErrorMessage))
        {
            errorMessages.Add(resourceServiceClientParseErrorMessage);
        }

        // Only validate resource service configuration if we have a URI to connect to.
        // If we do not, then the dashboard will run without resources, but still show OTEL data.
        if (options.ResourceServiceClient.GetUri() != null)
        {
            switch (options.ResourceServiceClient.AuthMode)
            {
                case ResourceClientAuthMode.Unsecured:
                    break;
                case ResourceClientAuthMode.ApiKey:
                    if (string.IsNullOrWhiteSpace(options.ResourceServiceClient.ApiKey))
                    {
                        errorMessages.Add($"{DashboardConfigNames.ResourceServiceClientAuthModeName.ConfigKey} is \"{nameof(ResourceClientAuthMode.ApiKey)}\", but no {DashboardConfigNames.ResourceServiceClientApiKeyName.ConfigKey} is configured.");
                    }
                    break;
                case ResourceClientAuthMode.Certificate:
                    switch (options.ResourceServiceClient.ClientCertificate.Source)
                    {
                        case DashboardClientCertificateSource.File:
                            if (string.IsNullOrEmpty(options.ResourceServiceClient.ClientCertificate.FilePath))
                            {
                                errorMessages.Add($"{DashboardConfigNames.ResourceServiceClientCertificateSourceName.ConfigKey} is \"File\", but no {DashboardConfigNames.ResourceServiceClientCertificateFilePathName.ConfigKey} is configured.");
                            }
                            break;
                        case DashboardClientCertificateSource.KeyStore:
                            if (string.IsNullOrEmpty(options.ResourceServiceClient.ClientCertificate.Subject))
                            {
                                errorMessages.Add($"{DashboardConfigNames.ResourceServiceClientCertificateSourceName.ConfigKey} is \"KeyStore\", but no {DashboardConfigNames.ResourceServiceClientCertificateSubjectName.ConfigKey} is configured.");
                            }
                            break;
                        case null:
                            errorMessages.Add($"The resource service client is configured to use certificates, but no certificate source is specified. Specify {DashboardConfigNames.ResourceServiceClientCertificateSourceName.ConfigKey}. Possible values: {string.Join(", ", typeof(DashboardClientCertificateSource).GetEnumNames())}");
                            break;
                        default:
                            errorMessages.Add($"Unexpected resource service client certificate source: {options.ResourceServiceClient.ClientCertificate.Source}");
                            break;
                    }
                    break;
                case null:
                    errorMessages.Add($"Resource service client authentication is not configured. Specify {DashboardConfigNames.ResourceServiceClientAuthModeName.ConfigKey}. Possible values: {string.Join(", ", typeof(ResourceClientAuthMode).GetEnumNames())}");
                    break;
                default:
                    errorMessages.Add($"Unexpected resource service client authentication mode: {options.ResourceServiceClient.AuthMode}");
                    break;
            }
        }

        if (!options.DebugSession.TryParseOptions(out var debugSessionParseErrorMessage))
        {
            errorMessages.Add(debugSessionParseErrorMessage);
        }

        return errorMessages.Count > 0
            ? ValidateOptionsResult.Fail(errorMessages)
            : ValidateOptionsResult.Success;
    }
}
