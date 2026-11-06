// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components;

public partial class TraceActions : ComponentBase
{
    private AspireMenuButton? _menuButton;

    [Inject]
    public required TraceMenuBuilder TraceMenuBuilder { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsLoc { get; init; }

    [Parameter]
    public required TraceSummary Summary { get; set; }

    /// <summary>
    /// The resources layout hosting the trace list, if any. It builds the same contextual detail URL as a row click.
    /// </summary>
    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    private IList<MenuButtonItem> GetMenuItems()
    {
        var menuItems = new List<MenuButtonItem>();
        // Items are built when the menu opens, so the URL reflects the list's current selection and filters.
        TraceMenuBuilder.AddMenuItems(menuItems, Summary,
            ResourcesLayout?.GetTraceDetailUrl(Summary.TraceId) ?? DashboardUrls.TraceDetailUrl(Summary.TraceId));
        return menuItems;
    }
}
