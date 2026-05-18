// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Aspire.Hosting;

internal static class LoggingHelpers
{
    public static void WriteDashboardSummary(ILogger logger, string? dashboardUrls, string? otlpGrpcUrls, string? otlpHttpUrls, string? token, bool isContainer = false)
    {
        if (!StringUtils.TryGetUriFromDelimitedString(dashboardUrls, ";", out var firstDashboardUrl))
        {
            return;
        }

        static string? GetEndpointAuthority(string? urls)
        {
            return StringUtils.TryGetUriFromDelimitedString(urls, ";", out var firstUrl)
                ? firstUrl.GetLeftPart(UriPartial.Authority)
                : null;
        }

        var dashboardUrl = firstDashboardUrl.GetLeftPart(UriPartial.Authority);
        var otlpGrpcUrl = GetEndpointAuthority(otlpGrpcUrls);
        var otlpHttpUrl = GetEndpointAuthority(otlpHttpUrls);
        var loginUrl = !string.IsNullOrEmpty(token)
            ? $"{dashboardUrl}/login?t={token}"
            : null;

        var templateBuilder = new StringBuilder();
        var parameters = new List<object?>();

        templateBuilder
            .Append("Aspire Dashboard").Append('\n')
            .Append('\n')
            .Append("Dashboard:    {DashboardUrl}").Append('\n');
        parameters.Add(dashboardUrl);

        if (loginUrl is not null)
        {
            templateBuilder.Append("Login URL:    {LoginUrl}").Append('\n');
            parameters.Add(loginUrl);
        }

        if (otlpGrpcUrl is not null)
        {
            templateBuilder.Append("OTLP/gRPC:    {OtlpGrpcUrl}").Append('\n');
            parameters.Add(otlpGrpcUrl);
        }

        if (otlpHttpUrl is not null)
        {
            templateBuilder.Append("OTLP/HTTP:    {OtlpHttpUrl}").Append('\n');
            parameters.Add(otlpHttpUrl);
        }

        if (isContainer)
        {
            templateBuilder.Append('\n');
            templateBuilder.Append("URLs may need changes depending on how network access to the container is configured.").Append('\n');
        }

        logger.LogInformation(templateBuilder.ToString(), parameters.ToArray());
    }
}
