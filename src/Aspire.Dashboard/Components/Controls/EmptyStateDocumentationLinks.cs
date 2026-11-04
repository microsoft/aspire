// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.WebUtilities;

namespace Aspire.Dashboard.Components.Controls;

public enum EmptyStateDocumentationPanel
{
    Endpoints,
    Console,
    StructuredLogs,
    Traces,
    Metrics,
    Relationships,
    Properties,
    EnvironmentVariables,
    Volumes,
    HealthChecks
}

public sealed record EmptyStateDocumentationArticle(string TitleResourceName, string Url);

public static class EmptyStateDocumentationLinks
{
    private const string ServiceDefaultsOpenTelemetryUrl = "https://aspire.dev/get-started/csharp-service-defaults/#opentelemetry-configuration";
    private const string CustomResourcesUrl = "https://aspire.dev/extensibility/custom-resources/";

    private static readonly IReadOnlyDictionary<string, string?> s_attributionParameters = new Dictionary<string, string?>
    {
        ["utm_source"] = "aspire-dashboard",
        ["utm_medium"] = "product",
        ["utm_campaign"] = "resource-empty-state"
    };

    public static EmptyStateDocumentationArticle GetArticle(EmptyStateDocumentationPanel panel)
    {
        var (titleResourceName, url) = panel switch
        {
            EmptyStateDocumentationPanel.Endpoints => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewNetworkingDocumentationTitle), "https://aspire.dev/fundamentals/networking-overview/#how-endpoints-work"),
            EmptyStateDocumentationPanel.Console => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewResourceModelDocumentationTitle), "https://aspire.dev/architecture/resource-model/#resource-logging"),
            EmptyStateDocumentationPanel.StructuredLogs => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewServiceDefaultsDocumentationTitle), ServiceDefaultsOpenTelemetryUrl),
            EmptyStateDocumentationPanel.Traces => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewServiceDefaultsDocumentationTitle), ServiceDefaultsOpenTelemetryUrl),
            EmptyStateDocumentationPanel.Metrics => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewServiceDefaultsDocumentationTitle), ServiceDefaultsOpenTelemetryUrl),
            EmptyStateDocumentationPanel.Relationships => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewCustomResourcesDocumentationTitle), $"{CustomResourcesUrl}#resource-relationships"),
            EmptyStateDocumentationPanel.Properties => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewCustomResourcesDocumentationTitle), $"{CustomResourcesUrl}#initial-state-configuration"),
            EmptyStateDocumentationPanel.EnvironmentVariables => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewEnvironmentVariablesDocumentationTitle), "https://aspire.dev/fundamentals/environment-variables/"),
            EmptyStateDocumentationPanel.Volumes => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewVolumesDocumentationTitle), "https://aspire.dev/fundamentals/persist-data-volumes/"),
            EmptyStateDocumentationPanel.HealthChecks => (nameof(Aspire.Dashboard.Resources.Resources.ResourceOverviewHealthChecksDocumentationTitle), "https://aspire.dev/fundamentals/health-checks/#apphost-resource-health-checks"),
            _ => throw new ArgumentOutOfRangeException(nameof(panel), panel, null)
        };

        return new EmptyStateDocumentationArticle(titleResourceName, AddAttribution(url));
    }

    internal static string AddAttribution(string url)
    {
        var fragmentIndex = url.IndexOf('#', StringComparison.Ordinal);
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
        var urlWithoutFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;

        return QueryHelpers.AddQueryString(urlWithoutFragment, s_attributionParameters) + fragment;
    }
}
