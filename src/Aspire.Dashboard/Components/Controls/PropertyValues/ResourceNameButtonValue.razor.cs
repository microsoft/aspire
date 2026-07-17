// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

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

    [Inject]
    private DashboardDataSource DataSource { get; set; } = null!;

    [Inject]
    public required IconResolver IconResolver { get; init; }

    private ResourceViewModel? _resource;
    private Icon? _resourceIcon;

    protected override void OnParametersSet()
    {
        _resourceIcon = null;

        if (DashboardClient.IsEnabled)
        {
            _resource = DataSource.ResourceRepository.GetResource(Resource.ResourceKey.ToString());
            if (_resource != null)
            {
                _resourceIcon = ResourceIconHelpers.GetIconForResource(IconResolver, _resource, IconSize.Size16, IconVariant.Regular);
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
