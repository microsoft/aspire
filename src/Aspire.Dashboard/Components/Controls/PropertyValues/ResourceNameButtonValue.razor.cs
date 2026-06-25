// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Components.Deck;
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
    private DeckIconName? _resourceIconName;

    protected override void OnParametersSet()
    {
        _resourceIconName = null;

        if (DashboardClient.IsEnabled)
        {
            _resource = DashboardClient.GetResource(Resource.ResourceKey.ToString());
            if (_resource != null)
            {
                _resourceIconName = ResourceIconHelpers.GetDeckIconForResource(_resource);
            }
        }
    }

    private Task OnClickAsync()
    {
        Debug.Assert(_resource != null, "Should only get here if there is a matched resource.");

        NavigationManager.NavigateTo(DashboardUrls.ResourcesUrl(_resource.Name));
        return Task.CompletedTask;
    }
}
