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
    private string? _resourceName;
    private string? _revealedSourcePropertyName;

    [Inject]
    public required IStringLocalizer<ResourceStrings> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Columns> ColumnsLoc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Parameter]
    public ResourceViewModel? Resource { get; set; }

    [Parameter]
    public string? TelemetrySourceName { get; set; }

    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    [Parameter]
    public string? Style { get; set; }

    [Parameter, EditorRequired]
    public bool HideCommandButtons { get; set; }

    private IEnumerable<ResourceViewModel> Resources => ResourcesLayout?.ResourceByName.Values ?? [];

    protected override void OnParametersSet()
    {
        switch (Resource, TelemetrySourceName)
        {
            case ({ } resource, null):
                SetResourceProperties(resource);
                break;
            case (null, { } sourceName):
                _resourceName = null;
                _revealedSourcePropertyName = null;
                _properties =
                [
                    CreateDisplayNameProperty(sourceName),
                    new(KnownProperties.Resource.ResourceTypeDisplayName, Loc[nameof(ResourceStrings.ResourcesTypeColumnHeader)], Loc[nameof(ResourceStrings.TelemetrySourceOverviewType)])
                ];
                break;
            default:
                throw new InvalidOperationException($"Specify either {nameof(Resource)} or {nameof(TelemetrySourceName)}.");
        }
    }

    private void SetResourceProperties(ResourceViewModel resource)
    {
        if (!string.Equals(resource.Name, _resourceName, StringComparisons.ResourceName))
        {
            _resourceName = resource.Name;
            _revealedSourcePropertyName = null;
        }

        _properties = [CreateDisplayNameProperty(resource.DisplayName)];
        if (!string.IsNullOrWhiteSpace(resource.State))
        {
            var stateText = ResourceStateViewModel.GetStateViewModel(resource, ColumnsLoc).Text;
            _properties.Add(new(KnownProperties.Resource.State, Loc[nameof(ResourceStrings.ResourcesDetailsStateProperty)], stateText, StatusPropertyKind.State));
            var description = ResourceStateViewModel.GetResourceStateTooltip(resource, ColumnsLoc, Resources);
            if (!string.IsNullOrWhiteSpace(description) && !string.Equals(description, stateText, StringComparison.Ordinal))
            {
                _properties.Add(new(StateDescriptionPropertyKey, ControlsLoc[nameof(ControlsStrings.ResourceDetailsStateDescriptionHeader)], description));
            }
        }
        if (!resource.IsParameter && resource.HealthStatus is { } health)
        {
            _properties.Add(new(KnownProperties.Resource.HealthState, Loc[nameof(ResourceStrings.ResourcesDetailsHealthStateProperty)], health.ToString(), StatusPropertyKind.HealthState));
        }
        _properties.Add(new(KnownProperties.Resource.ResourceTypeDisplayName, Loc[nameof(ResourceStrings.ResourcesTypeColumnHeader)], resource.ResourceTypeDisplayName));
        if (resource.IsParameter)
        {
            return;
        }

        if (resource.IsPersistentContainer)
        {
            _properties.Add(new(KnownProperties.Container.Lifetime, Loc[nameof(ResourceStrings.ResourcesDetailsContainerLifetimeProperty)],
                Loc[nameof(ResourceStrings.ResourcePersistentLifetime)], StatusPropertyKind.Lifetime,
                Loc[nameof(ResourceStrings.ResourcePersistentLifetimeDescription)]));
        }
        if (resource.StartTimeStamp is { } start)
        {
            _properties.Add(new(KnownProperties.Resource.StartTime, Loc[nameof(ResourceStrings.ResourcesDetailsStartTimeProperty)], FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, start),
                RawValue: resource.GetResourcePropertyValue(KnownProperties.Resource.StartTime) ?? start.ToString("O", CultureInfo.InvariantCulture)));
        }
        if (resource.StopTimeStamp is { } stop)
        {
            _properties.Add(new(KnownProperties.Resource.StopTime, Loc[nameof(ResourceStrings.ResourcesDetailsStopTimeProperty)], FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, stop)));
        }
        if (resource.TryGetExitCode(out var exitCode))
        {
            _properties.Add(new(KnownProperties.Resource.ExitCode, Loc[nameof(ResourceStrings.ResourcesDetailsExitCodeProperty)], exitCode.ToString(CultureInfo.CurrentCulture),
                exitCode is 0 ? StatusPropertyKind.Text : StatusPropertyKind.ExitError));
        }
        if (ResourceSourceViewModel.GetSourceViewModel(resource) is { } source)
        {
            _properties.Add(new(KnownProperties.Resource.Source, Loc[nameof(ResourceStrings.ResourcesSourceColumnHeader)], source.Value, StatusPropertyKind.Source,
                source.Tooltip, RawValue: source.OriginalValue, SourceProperty: source.Property)
            {
                IsValueMasked = source.Property.IsValueSensitive &&
                    !string.Equals(_revealedSourcePropertyName, source.Property.Name, StringComparisons.ResourcePropertyName)
            });
        }
    }

    // Live resource updates replace the property model, so keep the reveal choice on the card.
    private void OnSourceMaskedChanged(StatusProperty property) =>
        _revealedSourcePropertyName = property.IsValueMasked ? null : property.SourceProperty?.Name;

    private string FormatName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, Resources);

    private StatusProperty CreateDisplayNameProperty(string name) =>
        new(KnownProperties.Resource.DisplayName, Loc[nameof(ResourceStrings.ResourcesDetailsDisplayNameProperty)], name, StatusPropertyKind.DisplayName);

    private sealed record StatusProperty(string PropertyKey, string Name, string Text, StatusPropertyKind Kind = StatusPropertyKind.Text,
        string? Tooltip = null, string? RawValue = null, ResourcePropertyViewModel? SourceProperty = null) : IPropertyGridItem
    {
        object IPropertyGridItem.Key => PropertyKey;
        string? IPropertyGridItem.Value => Kind == StatusPropertyKind.Text ? Text : null;
        string? IPropertyGridItem.ValueTooltip => Kind == StatusPropertyKind.Source ? Tooltip : null;
        string IPropertyGridItem.ValueToVisualize => RawValue ?? Text;
        bool IPropertyGridItem.TextVisualizerContainsSecret => SourceProperty?.IsValueSensitive == true;
        bool IPropertyGridItem.IsValueSensitive => SourceProperty?.IsValueSensitive == true;
        public bool IsValueMasked { get; set; }
    }
}

// Keep this enum outside the component so Blazor's OpenComponent does not preserve its
// inherited Enum.GetValues(Type), which requires dynamic code under Native AOT.
internal enum StatusPropertyKind
{
    Text,
    DisplayName,
    State,
    HealthState,
    Lifetime,
    ExitError,
    Source
}
