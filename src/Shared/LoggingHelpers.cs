// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace Aspire.Hosting;

internal static class LoggingHelpers
{
    public static void WriteDashboardSummary(ILogger logger, string? dashboardUrl, string? otlpGrpcUrl, string? otlpHttpUrl, string? token, bool isContainer = false)
    {
        // Callers should pass a single resolved URL, not a semicolon-delimited list.
        AssertSingleUrl(dashboardUrl, nameof(dashboardUrl));
        AssertSingleUrl(otlpGrpcUrl, nameof(otlpGrpcUrl));
        AssertSingleUrl(otlpHttpUrl, nameof(otlpHttpUrl));

        static string? GetAuthority(string? url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : null;
        }

        var dashboardAuthority = GetAuthority(dashboardUrl);
        var otlpGrpcAuthority = GetAuthority(otlpGrpcUrl);
        var otlpHttpAuthority = GetAuthority(otlpHttpUrl);

        // Nothing to log if we have no URLs at all.
        if (dashboardAuthority is null && otlpGrpcAuthority is null && otlpHttpAuthority is null)
        {
            return;
        }

        var loginUrl = !string.IsNullOrEmpty(token) && dashboardAuthority is not null
            ? $"{dashboardAuthority}/login?t={token}"
            : null;

        var templateBuilder = new StringBuilder();
        var parameters = new List<object?>();

        templateBuilder
            .Append("Aspire Dashboard").Append('\n')
            .Append('\n');

        if (dashboardAuthority is not null)
        {
            templateBuilder.Append("Dashboard:    {DashboardUrl}").Append('\n');
            parameters.Add(dashboardAuthority);
        }

        if (loginUrl is not null)
        {
            templateBuilder.Append("Login URL:    {LoginUrl}").Append('\n');
            parameters.Add(loginUrl);
        }

        if (otlpGrpcAuthority is not null)
        {
            templateBuilder.Append("OTLP/gRPC:    {OtlpGrpcUrl}").Append('\n');
            parameters.Add(otlpGrpcAuthority);
        }

        if (otlpHttpAuthority is not null)
        {
            templateBuilder.Append("OTLP/HTTP:    {OtlpHttpUrl}").Append('\n');
            parameters.Add(otlpHttpAuthority);
        }

        if (isContainer)
        {
            templateBuilder.Append('\n');
            templateBuilder.Append("URLs may need changes depending on how network access to the container is configured.").Append('\n');
        }

        logger.LogInformation(templateBuilder.ToString(), parameters.ToArray());
    }

    [Conditional("DEBUG")]
    private static void AssertSingleUrl(string? url, string paramName)
    {
        Debug.Assert(url is null || !url.Contains(';'), $"{paramName} should not contain ';': {url}");
    }
}
