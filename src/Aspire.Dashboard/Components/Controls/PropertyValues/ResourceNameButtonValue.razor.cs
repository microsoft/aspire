// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.PropertyValues;

public partial class ResourceNameButtonValue
{
    [Parameter, EditorRequired]
    public required string Value { get; set; }

    [Parameter, EditorRequired]
    public required string HighlightText { get; set; }

    [Parameter, EditorRequired]
    public required OtlpResource Resource { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    private ResourceViewModel? _resource;
    protected override void OnParametersSet()
    {
        _resource = null;

        if (DashboardClient.IsEnabled)
        {
            _resource = DashboardClient.GetResource(Resource.ResourceKey.ToString());
        }
    }

    private Task OnClickAsync()
    {
        Debug.Assert(_resource != null, "Should only get here if there is a matched resource.");

        NavigationManager.NavigateTo(DashboardUrls.ResourcesUrl(_resource.Name));
        return Task.CompletedTask;
    }
}
