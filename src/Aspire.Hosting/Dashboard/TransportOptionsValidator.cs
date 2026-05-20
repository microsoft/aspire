// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dashboard;

internal class TransportOptionsValidator(IConfiguration configuration, DistributedApplicationExecutionContext executionContext, DistributedApplicationOptions distributedApplicationOptions) : IValidateOptions<TransportOptions>
{
    public ValidateOptionsResult Validate(string? name, TransportOptions transportOptions)
    {
        var effectiveAllowUnsecureTransport = transportOptions.AllowUnsecureTransport || distributedApplicationOptions.DisableDashboard || distributedApplicationOptions.AllowUnsecuredTransport;

        if (executionContext.IsPublishMode || effectiveAllowUnsecureTransport)
        {
            return ValidateOptionsResult.Success;
        }

        // Validate ASPNETCORE_URLS
        var applicationUrls = configuration[KnownConfigNames.AspNetCoreUrls];
        if (!string.IsNullOrEmpty(applicationUrls))
        {
            var firstApplicationUrl = applicationUrls.Split(";").First();

            if (!TryParseBindingAddress(firstApplicationUrl, out var parsedFirstApplicationUrl))
            {
                return ValidateOptionsResult.Fail($"The 'applicationUrl' setting of the launch profile has value '{firstApplicationUrl}' which could not be parsed as a URI.");
            }

            if (parsedFirstApplicationUrl.Scheme == "http")
            {
                return ValidateOptionsResult.Fail($"The 'applicationUrl' setting must be an https address unless the '{KnownConfigNames.AllowUnsecuredTransport}' environment variable is set to true. This configuration is commonly set in the launch profile. See https://aka.ms/aspire/allowunsecuredtransport for more details.");
            }
        }

        // Validate ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL
        var dashboardOtlpGrpcEndpointUrl = configuration.GetString(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl);
        var dashboardOtlpHttpEndpointUrl = configuration.GetString(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl);

        if (!TryValidateEndpointUrl(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, dashboardOtlpGrpcEndpointUrl, out var resultGrpc))
        {
            return resultGrpc;
        }
        if (!TryValidateEndpointUrl(KnownConfigNames.DashboardOtlpHttpEndpointUrl, dashboardOtlpHttpEndpointUrl, out var resultHttp))
        {
            return resultHttp;
        }

        // Validate ASPIRE_DASHBOARD_RESOURCE_SERVER_ENDPOINT_URL
        var resourceServiceEndpointUrl = configuration.GetString(KnownConfigNames.ResourceServiceEndpointUrl, KnownConfigNames.Legacy.ResourceServiceEndpointUrl);
        if (!string.IsNullOrEmpty(resourceServiceEndpointUrl))
        {
            if (!Uri.TryCreate(resourceServiceEndpointUrl, UriKind.Absolute, out var parsedResourceServiceEndpointUrl))
            {
                return ValidateOptionsResult.Fail($"The {KnownConfigNames.ResourceServiceEndpointUrl} setting with a value of '{resourceServiceEndpointUrl}' could not be parsed as a URI.");
            }

            if (parsedResourceServiceEndpointUrl.Scheme == "http")
            {
                return ValidateOptionsResult.Fail($"The '{KnownConfigNames.ResourceServiceEndpointUrl}' setting must be an https address unless the '{KnownConfigNames.AllowUnsecuredTransport}' environment variable is set to true. This configuration is commonly set in the launch profile. See https://aka.ms/aspire/allowunsecuredtransport for more details.");
            }
        }

        return ValidateOptionsResult.Success;

        static bool TryParseBindingAddress(string address, [NotNullWhen(true)] out BindingAddress? bindingAddress)
        {
            try
            {
                bindingAddress = BindingAddress.Parse(address);
                return true;
            }
            catch
            {
                bindingAddress = null;
                return false;
            }
        }

        static bool TryValidateEndpointUrl(string configName, string? value, [NotNullWhen(false)] out ValidateOptionsResult? result)
        {
            if (!string.IsNullOrEmpty(value))
            {
                if (!TryParseBindingAddress(value, out var parsedBindingAddress))
                {
                    result = ValidateOptionsResult.Fail($"The {configName} setting with a value of '{value}' could not be parsed as a URI.");
                    return false;
                }

                if (parsedBindingAddress.Scheme == "http")
                {
                    result = ValidateOptionsResult.Fail($"The '{configName}' setting must be an https address unless the '{KnownConfigNames.AllowUnsecuredTransport}' environment variable is set to true. This configuration is commonly set in the launch profile. See https://aka.ms/aspire/allowunsecuredtransport for more details.");
                    return false;
                }
            }

            result = null;
            return true;
        }
    }
}
