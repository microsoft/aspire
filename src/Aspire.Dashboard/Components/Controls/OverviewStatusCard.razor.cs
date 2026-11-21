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
    private const string StateDescriptionPropertyKey = "resource-state-description";

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
        _properties = [new(KnownProperties.Resource.DisplayName, Loc[nameof(ResourceStrings.ResourcesDetailsDisplayNameProperty)], Resource.DisplayName, StatusPropertyKind.DisplayName)];
        if (!string.IsNullOrWhiteSpace(Resource.State))
        {
            var stateText = ResourceStateViewModel.GetStateViewModel(Resource, ColumnsLoc).Text;
            _properties.Add(new(KnownProperties.Resource.State, Loc[nameof(ResourceStrings.ResourcesDetailsStateProperty)], stateText, StatusPropertyKind.State));
            var description = ResourceStateViewModel.GetResourceStateTooltip(Resource, ColumnsLoc, Resources);
            if (!string.IsNullOrWhiteSpace(description) && !string.Equals(description, stateText, StringComparison.Ordinal))
            {
                _properties.Add(new(StateDescriptionPropertyKey, ControlsLoc[nameof(ControlsStrings.ResourceDetailsStateDescriptionHeader)], description));
            }
        }
        if (!Resource.IsParameter && Resource.HealthStatus is { } health)
        {
            _properties.Add(new(KnownProperties.Resource.HealthState, Loc[nameof(ResourceStrings.ResourcesDetailsHealthStateProperty)], health.ToString(), StatusPropertyKind.HealthState));
        }
        _properties.Add(new(KnownProperties.Resource.ResourceTypeDisplayName, Loc[nameof(ResourceStrings.ResourcesTypeColumnHeader)], Resource.ResourceTypeDisplayName));
        if (Resource.IsParameter)
        {
            return;
        }

        if (Resource.IsPersistentContainer)
        {
            _properties.Add(new(KnownProperties.Container.Lifetime, Loc[nameof(ResourceStrings.ResourcesDetailsContainerLifetimeProperty)],
                Loc[nameof(ResourceStrings.ResourcePersistentLifetime)], StatusPropertyKind.Lifetime,
                Loc[nameof(ResourceStrings.ResourcePersistentLifetimeDescription)]));
        }
        if (Resource.StartTimeStamp is { } start)
        {
            _properties.Add(new(KnownProperties.Resource.StartTime, Loc[nameof(ResourceStrings.ResourcesDetailsStartTimeProperty)], FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, start),
                RawValue: Resource.GetResourcePropertyValue(KnownProperties.Resource.StartTime) ?? start.ToString("O", CultureInfo.InvariantCulture)));
        }
        if (Resource.StopTimeStamp is { } stop)
        {
            _properties.Add(new(KnownProperties.Resource.StopTime, Loc[nameof(ResourceStrings.ResourcesDetailsStopTimeProperty)], FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, stop)));
        }
        if (Resource.TryGetExitCode(out var exitCode))
        {
            _properties.Add(new(KnownProperties.Resource.ExitCode, Loc[nameof(ResourceStrings.ResourcesDetailsExitCodeProperty)], exitCode.ToString(CultureInfo.CurrentCulture),
                exitCode is 0 ? StatusPropertyKind.Text : StatusPropertyKind.ExitError));
        }
        if (ResourceSourceViewModel.GetSourceViewModel(Resource) is { } source)
        {
            _properties.Add(new(KnownProperties.Resource.Source, Loc[nameof(ResourceStrings.ResourcesSourceColumnHeader)], source.Value, StatusPropertyKind.Source,
                source.Tooltip, RawValue: source.OriginalValue, SourceProperty: source.Property));
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

    private sealed record StatusProperty(string PropertyKey, string Name, string Text, StatusPropertyKind Kind = StatusPropertyKind.Text,
        string? Tooltip = null, string? RawValue = null, ResourcePropertyViewModel? SourceProperty = null) : IPropertyGridItem
    {
        object IPropertyGridItem.Key => PropertyKey;
        string? IPropertyGridItem.Value => Kind == StatusPropertyKind.Text ? Text : null;
        string? IPropertyGridItem.ValueTooltip => Kind == StatusPropertyKind.Source ? Tooltip : null;
        string IPropertyGridItem.ValueToVisualize => RawValue ?? Text;
        bool IPropertyGridItem.TextVisualizerContainsSecret => SourceProperty?.IsValueSensitive == true;
        bool IPropertyGridItem.IsValueSensitive => SourceProperty?.IsValueSensitive == true;
        bool IPropertyGridItem.IsValueMasked
        {
            get => SourceProperty?.IsValueMasked == true;
            set => (SourceProperty ?? throw new InvalidOperationException("Only the Source row supports masking.")).IsValueMasked = value;
        }
    }
}
