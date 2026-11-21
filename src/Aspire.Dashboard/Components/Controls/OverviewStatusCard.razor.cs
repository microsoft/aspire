// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using ResourceStrings = Aspire.Dashboard.Resources.Resources;

namespace Aspire.Dashboard.Components.Controls;

public partial class OverviewStatusCard
{
    private List<StatusProperty> _properties = [];

    [Inject]
    public required IStringLocalizer<ResourceStrings> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Columns> ColumnsLoc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    [Parameter]
    public string? Style { get; set; }

    [Parameter, EditorRequired]
    public bool HideCommandButtons { get; set; }

    private IEnumerable<ResourceViewModel> Resources => ResourcesLayout?.ResourceByName.Values ?? [];

    protected override void OnParametersSet()
    {
        _properties = [new(Loc[nameof(ResourceStrings.ResourcesDetailsDisplayNameProperty)], Resource.DisplayName, StatusPropertyKind.DisplayName)];
        if (!string.IsNullOrWhiteSpace(Resource.State))
        {
            var stateText = ResourceStateViewModel.GetStateViewModel(Resource, ColumnsLoc).Text;
            _properties.Add(new(Loc[nameof(ResourceStrings.ResourcesDetailsStateProperty)], stateText, StatusPropertyKind.State));
            var description = ResourceStateViewModel.GetResourceStateTooltip(Resource, ColumnsLoc, Resources);
            if (!string.IsNullOrWhiteSpace(description) && !string.Equals(description, stateText, StringComparison.Ordinal))
            {
                _properties.Add(new(ControlsLoc[nameof(ControlsStrings.ResourceDetailsStateDescriptionHeader)], description));
            }
        }
        if (Resource.HealthStatus is { } health)
        {
            _properties.Add(new(Loc[nameof(ResourceStrings.ResourcesDetailsHealthStateProperty)], health.ToString(), StatusPropertyKind.HealthState));
        }
        _properties.Add(new(Loc[nameof(ResourceStrings.ResourcesTypeColumnHeader)], Resource.ResourceTypeDisplayName));
        if (Resource.IsPersistentContainer)
        {
            _properties.Add(new(Loc[nameof(ResourceStrings.ResourcesDetailsContainerLifetimeProperty)],
                Loc[nameof(ResourceStrings.ResourcePersistentLifetime)], StatusPropertyKind.Lifetime,
                Loc[nameof(ResourceStrings.ResourcePersistentLifetimeDescription)]));
        }
        if (Resource.StartTimeStamp is { } start)
        {
            _properties.Add(new(Loc[nameof(ResourceStrings.ResourcesDetailsStartTimeProperty)], FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, start)));
        }
        if (Resource.StopTimeStamp is { } stop)
        {
            _properties.Add(new(Loc[nameof(ResourceStrings.ResourcesDetailsStopTimeProperty)], FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, stop)));
        }
        if (Resource.TryGetExitCode(out var exitCode))
        {
            _properties.Add(new(Loc[nameof(ResourceStrings.ResourcesDetailsExitCodeProperty)], exitCode.ToString(CultureInfo.CurrentCulture),
                exitCode is 0 ? StatusPropertyKind.Text : StatusPropertyKind.ExitError));
        }
        if (ResourceSourceViewModel.GetSourceViewModel(Resource) is { } source)
        {
            _properties.Add(new(Loc[nameof(ResourceStrings.ResourcesSourceColumnHeader)], source.Value, StatusPropertyKind.Source, source.Tooltip));
        }
    }

    private string FormatName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, Resources);

    private enum StatusPropertyKind
    {
        Text,
        DisplayName,
        State,
        HealthState,
        Lifetime,
        ExitError,
        Source
    }

    private sealed record StatusProperty(string Name, string Text, StatusPropertyKind Kind = StatusPropertyKind.Text, string? Tooltip = null) : IPropertyGridItem
    {
        string? IPropertyGridItem.Value => Kind == StatusPropertyKind.Text ? Text : null;
        string IPropertyGridItem.ValueToVisualize => Text;
    }
}
