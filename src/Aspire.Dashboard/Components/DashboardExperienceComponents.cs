// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

internal static class DashboardExperienceComponents
{
#if ASPIRE_LEGACY_DASHBOARD
    private const DynamicallyAccessedMemberTypes RuntimeActivatedComponentMembers =
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties;
    private const string DashboardAssemblyName = "Aspire.Dashboard";
#endif
    private const string LegacyNamespacePrefix = "Aspire.Dashboard.Components.Legacy.";
    private const string LegacyMainLayoutTypeName = $"{LegacyNamespacePrefix}Layout.LegacyMainLayout";
    private const string LegacyNotFoundTypeName = $"{LegacyNamespacePrefix}Pages.NotFound";

    private static readonly Type? s_legacyMainLayoutType = typeof(App).Assembly.GetType(LegacyMainLayoutTypeName);
    private static readonly Type? s_legacyNotFoundType = typeof(App).Assembly.GetType(LegacyNotFoundTypeName);

    internal static bool IsLegacyAvailable => s_legacyMainLayoutType is not null && s_legacyNotFoundType is not null;

    internal static Type GetDefaultLayout(Type pageType) =>
        IsLegacyPage(pageType) && s_legacyMainLayoutType is { } legacyLayout
            ? legacyLayout
            : typeof(Layout.MainLayout);

    internal static bool TryGetLegacyNotFound(
        NavigationManager navigationManager,
        [NotNullWhen(true)] out Type? layoutType,
        [NotNullWhen(true)] out Type? pageType)
    {
        layoutType = s_legacyMainLayoutType;
        pageType = s_legacyNotFoundType;
        return IsLegacyLocation(navigationManager) && layoutType is not null && pageType is not null;
    }

    private static bool IsLegacyPage(Type pageType) =>
        pageType.Namespace?.StartsWith(LegacyNamespacePrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Gets whether the current page belongs to the previous experience, for shared components that must keep main's
    /// behavior there, such as its resource URLs.
    /// </summary>
    internal static bool IsLegacyLocation(NavigationManager navigationManager)
    {
        // The base-relative path keeps the query and fragment, for example "legacy?view=Graph" on the legacy resources page.
        var path = navigationManager.ToBaseRelativePath(navigationManager.Uri);
        var endOfPath = path.IndexOfAny(['?', '#']);
        if (endOfPath >= 0)
        {
            path = path[..endOfPath];
        }

        return string.Equals(path, "legacy", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("legacy/", StringComparison.OrdinalIgnoreCase);
    }

#if ASPIRE_LEGACY_DASHBOARD
    [DynamicDependency(RuntimeActivatedComponentMembers, LegacyMainLayoutTypeName, DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.ConsoleLogs", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.Error", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.Login", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.Metrics", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, LegacyNotFoundTypeName, DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.Resources", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.StructuredLogs", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.Terminals", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.TerminalWindow", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.TraceDetail", DashboardAssemblyName)]
    [DynamicDependency(RuntimeActivatedComponentMembers, $"{LegacyNamespacePrefix}Pages.Traces", DashboardAssemblyName)]
#endif
    internal static void PreserveLegacyComponents()
    {
    }
}
