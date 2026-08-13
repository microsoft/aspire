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

        if (loginUrl is not null)
        {
            // dotnet watch looks for this exact log message to launch the dashboard. Do not change it.
            logger.LogInformation("Login to the dashboard at {LoginUrl}", loginUrl);
        }

        var templateBuilder = new StringBuilder();
        var parameters = new List<object?>();

        templateBuilder
            .Append("Aspire Dashboard").Append('\n')
            .Append('\n');

        // The default .NET console logger indents the first line 6 characters because of the level.
        // This prefix aligns the URLs under the dashboard title and makes them easier to read.
        // In other logging layouts the prefix is just extra spaces, but it doesn't cause any real issues.
        var prefix = "      ";

        if (dashboardAuthority is not null)
        {
            templateBuilder.Append(prefix).Append("- Dashboard:  {DashboardUrl}").Append('\n');
            parameters.Add(dashboardAuthority);
        }

        if (loginUrl is not null)
        {
            templateBuilder.Append(prefix).Append("- Login URL:  {LoginUrl}").Append('\n');
            parameters.Add(loginUrl);
        }

        if (otlpGrpcAuthority is not null)
        {
            templateBuilder.Append(prefix).Append("- OTLP/gRPC:  {OtlpGrpcUrl}").Append('\n');
            parameters.Add(otlpGrpcAuthority);
        }

        if (otlpHttpAuthority is not null)
        {
            templateBuilder.Append(prefix).Append("- OTLP/HTTP:  {OtlpHttpUrl}").Append('\n');
            parameters.Add(otlpHttpAuthority);
        }

        logger.LogInformation(templateBuilder.ToString(), parameters.ToArray());

        if (isContainer)
        {
            logger.LogInformation("Dashboard is running in a container. Access the dashboard from the host using port forwarding.");
        }
    }

    [Conditional("DEBUG")]
    private static void AssertSingleUrl(string? url, string paramName)
    {
        Debug.Assert(url is null || !url.Contains(';'), $"{paramName} should not contain ';': {url}");
    }
}
