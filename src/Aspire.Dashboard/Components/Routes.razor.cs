// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace Aspire.Dashboard.Components;

public partial class Routes
{
    private ViewportInformation? _viewportInformation;

    private static Type GetDefaultLayout(RouteData routeData) =>
        DashboardExperienceComponents.GetDefaultLayout(routeData.PageType);
}
