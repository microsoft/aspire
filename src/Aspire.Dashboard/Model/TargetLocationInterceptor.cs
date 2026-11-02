// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Model;

internal static class TargetLocationInterceptor
{
    public const string ResourcesPath = "/";
    public const string StructuredLogsPath = "/structuredlogs";
    public const string LegacyResourcesPath = "/legacy";
    public const string LegacyStructuredLogsPath = "/legacy/structuredlogs";

    /// <summary>
    /// Gets the page that replaces an app model page when there is no resource service, or null when the page doesn't
    /// need the resource service. The previous experience keeps main's behavior: only its resources page, which
    /// requires the resource service, goes to its structured logs page. That page is routed at both /legacy and
    /// /legacy/resources.
    /// </summary>
    public static string? GetStandaloneLocation(string path)
    {
        if (IsResourceServicePath(path))
        {
            return StructuredLogsPath;
        }

        var trimmedPath = path.TrimEnd('/');
        return string.Equals(trimmedPath, LegacyResourcesPath, StringComparisons.UrlPath)
            || string.Equals(trimmedPath, LegacyResourcesPath + "/resources", StringComparisons.UrlPath)
            ? LegacyStructuredLogsPath
            : null;
    }

    /// <summary>
    /// Returns a value indicating whether the path is a page that displays the app model. These pages can't be shown
    /// without a resource service, so they're redirected to structured logs.
    /// </summary>
    public static bool IsResourceServicePath(string path)
    {
        if (string.Equals(path, ResourcesPath, StringComparisons.UrlPath))
        {
            return true;
        }

        var trimmedPath = path.TrimStart('/');
        var firstSegment = trimmedPath.Split('/', 2)[0];

        return string.Equals(firstSegment, DashboardUrls.ResourceOverviewBasePath, StringComparisons.UrlPath)
            || (string.Equals(firstSegment, DashboardUrls.ParametersBasePath, StringComparisons.UrlPath) && trimmedPath.Length == firstSegment.Length)
            || (string.Equals(firstSegment, DashboardUrls.GraphBasePath, StringComparisons.UrlPath) && trimmedPath.Length == firstSegment.Length);
    }

    public static bool InterceptTargetLocation(string appBaseUri, string originalTargetLocation, [NotNullWhen(true)] out string? newTargetLocation)
    {
        string path;
        var uri = new Uri(originalTargetLocation, UriKind.RelativeOrAbsolute);

        // Location could be an absolute URL if clicking on link in the page.
        if (uri.IsAbsoluteUri)
        {
            // Don't want to modify the URL if it is to a different app.
            var targetBaseUri = new Uri(uri.GetLeftPart(UriPartial.Authority));
            if (targetBaseUri != new Uri(appBaseUri))
            {
                newTargetLocation = null;
                return false;
            }

            path = uri.AbsolutePath;
        }
        else
        {
            path = originalTargetLocation;
        }

        newTargetLocation = GetStandaloneLocation(path);
        return newTargetLocation is not null;
    }
}
