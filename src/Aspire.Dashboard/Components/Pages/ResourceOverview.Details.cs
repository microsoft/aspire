// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Controls.PropertyValues;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Extensions;
using ProtobufValue = Google.Protobuf.WellKnownTypes.Value;

namespace Aspire.Dashboard.Components.Pages;

// Properties, health checks, volumes and environment variables cards of the resource overview.
public sealed partial class ResourceOverview
{
    private const int DetailsPreviewCount = 10;

    // The status card already displays these properties, and the header displays the resource name.
    private static readonly HashSet<string> s_statusCardPropertyNames = new(StringComparers.ResourcePropertyName)
    {
        KnownProperties.Resource.DisplayName,
        KnownProperties.Resource.State,
        KnownProperties.Resource.HealthState,
        KnownProperties.Resource.Type,
        KnownProperties.Resource.StartTime,
        KnownProperties.Resource.StopTime,
        KnownProperties.Resource.ExitCode,
        KnownProperties.Resource.Source,
    };

    private readonly List<DisplayedResourcePropertyViewModel> _properties = new();
    private readonly MaskState _propertiesMask = new();
    private readonly MaskState _environmentMask = new();
    private ResourceViewModel? _detailsResource;
    private Dictionary<string, ComponentMetadata>? _propertyValueComponents;
    private bool _showAllProperties;
    private bool _showAllEnvironmentVariables;
    private string _environmentFilter = string.Empty;
    private bool _propertiesExpanded;
    private bool _environmentExpanded;

    /// <summary>
    /// Rebuilds the property view models when the selected resource is updated. The resource view model is replaced on
    /// every update, so masking choices are stored separately and reapplied to the new view models.
    /// </summary>
    private void UpdateDetails()
    {
        var resource = Resource;
        if (ReferenceEquals(resource, _detailsResource))
        {
            return;
        }

        if (!string.Equals(resource?.Name, _detailsResource?.Name, StringComparisons.ResourceName))
        {
            _propertiesMask.Reset();
            _environmentMask.Reset();
            _environmentFilter = string.Empty;
            _propertiesExpanded = false;
            _environmentExpanded = false;
        }

        _detailsResource = resource;
        _properties.Clear();
        _propertyValueComponents = null;

        if (resource is null)
        {
            return;
        }

        foreach (var property in resource.Properties.Values)
        {
            if (s_statusCardPropertyNames.Contains(property.Name) || !HasValue(property.Value))
            {
                continue;
            }

            var displayedProperty = property;

            // An unresolved secret parameter has no value to hide, so keep the placeholder visible instead of
            // routing it through masking behavior.
            if (resource.HasMissingParameterValueState() &&
                string.Equals(property.Name, KnownProperties.Parameter.Value, StringComparisons.ResourcePropertyName) &&
                property.IsValueSensitive)
            {
                displayedProperty = new ResourcePropertyViewModel(
                    name: property.Name,
                    value: property.Value,
                    isValueSensitive: false,
                    knownProperty: property.KnownProperty,
                    sortOrder: property.SortOrder,
                    displayName: property.DisplayName,
                    isHighlighted: property.IsHighlighted);
            }

            _properties.Add(new DisplayedResourcePropertyViewModel(displayedProperty, Loc, TimeProvider));
        }

        _propertiesMask.Apply(_properties);
        _environmentMask.Apply(resource.Environment);

        // Render the same "Value not set" affordance as the parameters grid for parameters whose value is unset.
        if (resource.HasMissingParameterValueState())
        {
            var metadata = new ComponentMetadata
            {
                Type = typeof(ParameterValueDisplayCell),
                Parameters =
                {
                    ["Resource"] = resource,
                    ["OnExecuteCommandAsync"] = (Func<ResourceViewModel, CommandViewModel, Task>)ExecuteResourceCommandAsync,
                    ["IsCommandExecuting"] = (Func<ResourceViewModel, CommandViewModel, bool>)((r, command) => DashboardCommandExecutor.IsExecuting(r.Name, command.Name)),
                }
            };

            // New resource servers send the parameter value as a known property, while legacy fallback metadata
            // exposes it as an unknown property. Register both keys so the renderer works in both cases.
            _propertyValueComponents ??= [];
            _propertyValueComponents[KnownProperties.Parameter.Value] = metadata;
            _propertyValueComponents[DisplayedResourcePropertyViewModel.GetUnknownKey(KnownProperties.Parameter.Value)] = metadata;
        }

        // Link the container image to its registry page (Docker Hub or MCR) when we're confident of the URL.
        if (resource.TryGetContainerImage(out _))
        {
            var metadata = new ComponentMetadata { Type = typeof(ContainerImageValue) };

            // Resource servers with producer-supplied metadata send the container image as a known property,
            // while legacy fallback metadata (see LegacyResourcePropertyMetadata) also keys it by the same
            // name. Register both the known and unknown-property keys so the renderer works in both cases.
            _propertyValueComponents ??= [];
            _propertyValueComponents[KnownProperties.Container.Image] = metadata;
            _propertyValueComponents[DisplayedResourcePropertyViewModel.GetUnknownKey(KnownProperties.Container.Image)] = metadata;
        }
    }

    private static bool HasValue(ProtobufValue value) =>
        value is { HasNullValue: false } and not { KindCase: ProtobufValue.KindOneofCase.ListValue, ListValue.Values.Count: 0 };

    // Unknown properties are custom properties published by integrations. They're hidden by default, like in the
    // resource details panel, unless the integration marks them as highlighted.
    private static bool IsCoreProperty(DisplayedResourcePropertyViewModel property) => property.KnownProperty is not null || property.IsHighlighted;

    private bool HasNonCoreProperties => _properties.Any(p => !IsCoreProperty(p));

    private List<DisplayedResourcePropertyViewModel> GetVisibleProperties() =>
        _properties
            .Where(p => _showAllProperties || IsCoreProperty(p))
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCulture)
            .ToList();

    private bool HasSensitiveProperties => _properties.Any(p => ((IPropertyGridItem)p).IsValueSensitive);

    private void ToggleShowAllProperties() => _showAllProperties = !_showAllProperties;

    private void TogglePropertiesMasked() => _propertiesMask.Toggle(_properties);

    private void OnPropertyMaskedChanged(DisplayedResourcePropertyViewModel property) => _propertiesMask.OnItemChanged(_properties, property);

    private List<EnvironmentVariableViewModel> GetVisibleEnvironmentVariables(ResourceViewModel resource) =>
        resource.Environment
            .Where(e => (_showAllEnvironmentVariables || e.FromSpec) && ((IPropertyGridItem)e).MatchesFilter(_environmentFilter))
            .OrderBy(e => e.Name, StringComparers.EnvironmentVariableName)
            .ToList();

    private void TogglePropertiesExpanded() => _propertiesExpanded = !_propertiesExpanded;

    private void ToggleEnvironmentExpanded() => _environmentExpanded = !_environmentExpanded;

    private void ToggleShowAllEnvironmentVariables() => _showAllEnvironmentVariables = !_showAllEnvironmentVariables;

    private void ToggleEnvironmentMasked(ResourceViewModel resource) => _environmentMask.Toggle(resource.Environment);

    private void OnEnvironmentVariableMaskedChanged(ResourceViewModel resource, EnvironmentVariableViewModel variable) => _environmentMask.OnItemChanged(resource.Environment, variable);

    private string? GetStateDescription(ResourceViewModel resource, string stateText)
    {
        var description = ResourceStateViewModel.GetResourceStateTooltip(resource, ColumnsLoc, ResourcesLayout?.ResourceByName.Values);
        return string.IsNullOrWhiteSpace(description) || string.Equals(description, stateText, StringComparison.Ordinal) ? null : description;
    }

    private static List<HealthReportViewModel> GetHealthReports(ResourceViewModel resource) =>
        resource.HealthReports.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static List<VolumeViewModel> GetVolumes(ResourceViewModel resource) =>
        resource.Volumes.OrderBy(v => v.Source, StringComparer.OrdinalIgnoreCase).ToList();

    private async Task RevealVolumeSourceAsync(VolumeViewModel volume)
    {
        if (!VolumePathLauncher.TryReveal(volume.Source))
        {
            await ToastService.ShowErrorToastAsync(string.Format(CultureInfo.CurrentCulture, ControlsStringsLoc[nameof(Dashboard.Resources.ControlsStrings.VolumeRevealFailed)], volume.Source));
        }
    }

    /// <summary>
    /// Tracks whether the sensitive values of a card are masked. Individual values can be unmasked from the grid, so
    /// the state is either "all masked", "all unmasked" or a set of unmasked value names.
    /// </summary>
    private sealed class MaskState
    {
        private readonly HashSet<string> _unmaskedNames = new(StringComparer.Ordinal);
        private bool? _isAllMasked = true;

        public bool IsAllMasked => _isAllMasked ?? false;

        public void Reset()
        {
            _isAllMasked = true;
            _unmaskedNames.Clear();
        }

        public void Apply(IEnumerable<IPropertyGridItem> items)
        {
            foreach (var item in items.Where(static i => i.IsValueSensitive))
            {
                item.IsValueMasked = _isAllMasked ?? !_unmaskedNames.Contains(item.Name);
            }
        }

        public void Toggle(IEnumerable<IPropertyGridItem> items)
        {
            _isAllMasked = !IsAllMasked;
            _unmaskedNames.Clear();
            Apply(items);
        }

        public void OnItemChanged(IEnumerable<IPropertyGridItem> items, IPropertyGridItem item)
        {
            var maskedValues = items.Where(static i => i.IsValueSensitive).Select(static i => i.IsValueMasked).Distinct().ToList();
            if (maskedValues.Count == 1)
            {
                _isAllMasked = maskedValues[0];
                _unmaskedNames.Clear();
                return;
            }

            _isAllMasked = null;
            if (item.IsValueMasked)
            {
                _unmaskedNames.Remove(item.Name);
            }
            else
            {
                _unmaskedNames.Add(item.Name);
            }
        }
    }
}
