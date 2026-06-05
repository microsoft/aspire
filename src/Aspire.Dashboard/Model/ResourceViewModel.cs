// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Google.Protobuf.WellKnownTypes;
using Humanizer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

[DebuggerDisplay("Name = {Name}, ResourceType = {ResourceType}, State = {State}, Properties = {Properties.Count}")]
public sealed class ResourceViewModel
{
    private readonly ImmutableArray<HealthReportViewModel> _healthReports = [];
    private readonly KnownResourceState? _knownState;
    private Lazy<ImmutableArray<string>>? _cachedAddresses;

    public required string Name { get; init; }
    public required string ResourceType { get; init; }
    public required string DisplayName { get; init; }
    public required string Uid { get; init; }
    public required string? State { get; init; }
    public required string? StateStyle { get; init; }
    public required DateTime? CreationTimeStamp { get; init; }
    public required DateTime? StartTimeStamp { get; init; }
    public required DateTime? StopTimeStamp { get; init; }
    public required ImmutableArray<EnvironmentVariableViewModel> Environment { get; init; }
    public required ImmutableArray<UrlViewModel> Urls { get; init; }
    public required ImmutableArray<VolumeViewModel> Volumes { get; init; }
    public required ImmutableArray<RelationshipViewModel> Relationships { get; init; }
    public required ImmutableDictionary<string, ResourcePropertyViewModel> Properties { get; init; }
    public required ImmutableArray<CommandViewModel> Commands { get; init; }
    /// <summary>The health status of the resource. <see langword="null"/> indicates that health status is expected but not yet available.</summary>
    public HealthStatus? HealthStatus { get; private set; }
    public bool IsHidden { private get; init; }
    public bool SupportsDetailedTelemetry { get; init; }
    public string? IconName { get; init; }
    public IconVariant? IconVariant { get; init; }
    public bool IsParameter => string.Equals(ResourceType, KnownResourceTypes.Parameter, StringComparison.Ordinal);

    /// <summary>
    /// Gets the cached addresses for this resource that can be used for peer matching.
    /// This includes addresses extracted from URLs, connection strings, and parameter values.
    /// </summary>
    public ImmutableArray<string> CachedAddresses => (_cachedAddresses ??= new Lazy<ImmutableArray<string>>(ExtractResourceAddresses)).Value;

    private ImmutableArray<string> ExtractResourceAddresses()
    {
        var addresses = new List<string>();

        // Extract addresses from URL endpoints
        foreach (var service in Urls)
        {
            var hostAndPort = service.Url.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped);
            addresses.Add(hostAndPort);
        }

        // Extract addresses from connection strings using comprehensive parsing
        if (Properties.TryGetValue(KnownProperties.Resource.ConnectionString, out var connectionStringProperty) &&
            connectionStringProperty.Value.TryConvertToString(out var connectionString) &&
            ConnectionStringParser.TryDetectHostAndPort(connectionString, out var host, out var port))
        {
            var endpoint = port.HasValue ? $"{host}:{port.Value}" : host;
            addresses.Add(endpoint);
        }

        // Extract addresses from parameter values (for Parameter resources that contain URLs or host:port values)
        if (Properties.TryGetValue(KnownProperties.Parameter.Value, out var parameterValueProperty) &&
            parameterValueProperty.Value.TryConvertToString(out var parameterValue) &&
            ConnectionStringParser.TryDetectHostAndPort(parameterValue, out var parameterHost, out var parameterPort))
        {
            var parameterEndpoint = parameterPort.HasValue ? $"{parameterHost}:{parameterPort.Value}" : parameterHost;
            addresses.Add(parameterEndpoint);
        }

        return addresses.ToImmutableArray();
    }

    public required ImmutableArray<HealthReportViewModel> HealthReports
    {
        get => _healthReports;
        init
        {
            _healthReports = value;
            HealthStatus = ComputeHealthStatus(value, KnownState);
        }
    }

    public KnownResourceState? KnownState
    {
        get => _knownState;
        init
        {
            _knownState = value;
            HealthStatus = ComputeHealthStatus(_healthReports, value);
        }
    }

    internal bool MatchesFilter(string filter)
    {
        // TODO let ResourceType define the additional data values we include in searches
        return Name.Contains(filter, StringComparisons.UserTextSearch);
    }

    public string? GetResourcePropertyValue(string propertyName)
    {
        if (Properties.TryGetValue(propertyName, out var value))
        {
            if (value.Value.TryConvertToString(out var s))
            {
                return s;
            }
        }

        return null;
    }

    public bool IsResourceHidden(bool showHiddenResources)
    {
        if (showHiddenResources)
        {
            return false;
        }
        return IsHidden || KnownState is KnownResourceState.Hidden;
    }

    internal static HealthStatus? ComputeHealthStatus(ImmutableArray<HealthReportViewModel> healthReports, KnownResourceState? state)
    {
        if (state != KnownResourceState.Running)
        {
            return null;
        }

        return healthReports.Length == 0
            // If there are no health reports and the resource is running, assume it's healthy.
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy
            // If there are health reports, the health status is the minimum of the health status of the reports.
            // If any of the reports is null (first health check has not returned), the health status is unhealthy.
            : healthReports.MinBy(r => r.HealthStatus)?.HealthStatus
              ?? Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy;
    }

    public static string GetResourceName(ResourceViewModel resource, IDictionary<string, ResourceViewModel> allResources, bool showHiddenResources = false)
    {
        return GetResourceName(resource, allResources.Values);
    }

    public static string GetResourceName(ResourceViewModel resource, IEnumerable<ResourceViewModel> allResources, bool showHiddenResources = false)
    {
        var count = 0;
        foreach (var item in allResources)
        {
            if (item.IsResourceHidden(showHiddenResources))
            {
                continue;
            }

            if (string.Equals(item.DisplayName, resource.DisplayName, StringComparisons.ResourceName))
            {
                count++;
                if (count >= 2)
                {
                    // There are multiple resources with the same display name so they're part of a replica set.
                    // Need to use the name which has a unique ID to tell them apart.
                    return resource.Name;
                }
            }
        }

        return resource.DisplayName;
    }

    public static bool TryGetResourceByName(string resourceName, IDictionary<string, ResourceViewModel> resourceByName, [NotNullWhen(true)] out ResourceViewModel? resource)
    {
        if (resourceByName.TryGetValue(resourceName, out resource))
        {
            return true;
        }

        var resourcesWithDisplayName = resourceByName.Values.Where(r => string.Equals(resourceName, r.DisplayName, StringComparisons.ResourceName)).ToList();
        if (resourcesWithDisplayName.Count == 1)
        {
            resource = resourcesWithDisplayName.Single();
            return true;
        }

        return false;
    }
}

public sealed class ResourceViewModelNameComparer : IComparer<ResourceViewModel>
{
    public static readonly ResourceViewModelNameComparer Instance = new();

    public int Compare(ResourceViewModel? x, ResourceViewModel? y)
    {
        Debug.Assert(x != null);
        Debug.Assert(y != null);

        // Use display name by itself first.
        // This is to avoid the problem of using the full name where one resource is called "database" and another is called "database-admin".
        // The full names could end up "database-xyz" and "database-admin-xyz", which would put resources out of order.
        var displayNameResult = StringComparers.ResourceName.Compare(x.DisplayName, y.DisplayName);
        if (displayNameResult != 0)
        {
            return displayNameResult;
        }

        // Display names are the same so compare with full names.
        return StringComparers.ResourceName.Compare(x.Name, y.Name);
    }
}

[DebuggerDisplay("Name = {Name}, DisplayName = {DisplayName}")]
public sealed class CommandViewModel
{
    // Known resource command constants.
    // Keep in sync with KnownResourceCommands in Aspire.Hosting.
    public const string StartCommand = "resource-start";
    public const string StopCommand = "resource-stop";
    public const string RestartCommand = "resource-restart";
    private static readonly string[] s_knownResourceCommands = [StartCommand, StopCommand, RestartCommand];

    public string Name { get; }
    public CommandViewModelState State { get; }
    private string DisplayName { get; }
    private string DisplayDescription { get; }

    public string ConfirmationMessage { get; }
    public Value? Parameter { get; }
    public bool IsHighlighted { get; }
    public string IconName { get; }
    public IconVariant IconVariant { get; }

    public CommandViewModel(string name, CommandViewModelState state, string displayName, string displayDescription, string confirmationMessage, Value? parameter, bool isHighlighted, string iconName, IconVariant iconVariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        Name = name;
        State = state;
        DisplayName = displayName;
        DisplayDescription = displayDescription;
        ConfirmationMessage = confirmationMessage;
        Parameter = parameter;
        IsHighlighted = isHighlighted;
        IconName = iconName;
        IconVariant = iconVariant;
    }

    public static bool IsKnownCommand(string command)
    {
        return s_knownResourceCommands.Contains(command);
    }

    public string GetDisplayName(IStringLocalizer<Commands> loc)
    {
        return Name switch
        {
            StartCommand => loc[nameof(Commands.StartCommandDisplayName)],
            StopCommand => loc[nameof(Commands.StopCommandDisplayName)],
            RestartCommand => loc[nameof(Commands.RestartCommandDisplayName)],
            _ => DisplayName
        };
    }

    public string? GetDisplayDescription(IStringLocalizer<Commands> loc)
    {
        return Name switch
        {
            StartCommand => loc[nameof(Commands.StartCommandDisplayDescription)],
            StopCommand => loc[nameof(Commands.StopCommandDisplayDescription)],
            RestartCommand => loc[nameof(Commands.RestartCommandDisplayDescription)],
            _ => DisplayDescription is { Length: > 0 } ? DisplayDescription : null
        };
    }
}

public enum CommandViewModelState
{
    Enabled,
    Disabled,
    Hidden
}

[DebuggerDisplay("Name = {Name}, Value = {Value}, FromSpec = {FromSpec}, IsValueMasked = {IsValueMasked}")]
public sealed class EnvironmentVariableViewModel : IPropertyGridItem
{
    public string Name { get; }
    public string? Value { get; }
    public bool FromSpec { get; }

    public bool IsValueMasked { get; set; } = true;

    public bool IsValueSensitive => true;

    public EnvironmentVariableViewModel(string name, string? value, bool fromSpec)
    {
        // Name should always have a value, but somehow an empty/whitespace name can reach this point.
        // Better to allow the dashboard to run with an env var with no name than break when loading resources.
        // https://github.com/dotnet/aspire/issues/5309
        Name = name;
        Value = value;
        FromSpec = fromSpec;
    }
}

[DebuggerDisplay("{_propertyViewModel}")]
public sealed class DisplayedResourcePropertyViewModel : IPropertyGridItem
{
    private readonly Lazy<string> _displayValue;
    private readonly Lazy<string> _tooltip;

    private readonly string _key;
    private readonly ResourcePropertyViewModel _propertyViewModel;
    private readonly IStringLocalizer<Resources.Resources> _loc;
    private readonly BrowserTimeProvider _browserTimeProvider;

    public string ToolTip => _tooltip.Value;
    public KnownProperty? KnownProperty => _propertyViewModel.KnownProperty;
    public int Priority => _propertyViewModel.Priority;
    public Value Value => _propertyViewModel.Value;
    public string DisplayName => _propertyViewModel.KnownProperty?.GetDisplayName(_loc) ?? _propertyViewModel.Name;

    string IPropertyGridItem.Name => DisplayName;
    string? IPropertyGridItem.Value => _displayValue.Value;
    object IPropertyGridItem.Key => _key;

    bool IPropertyGridItem.IsValueSensitive => _propertyViewModel.IsValueSensitive;
    bool IPropertyGridItem.IsValueMasked { get => _propertyViewModel.IsValueMasked; set => _propertyViewModel.IsValueMasked = value; }

    public DisplayedResourcePropertyViewModel(ResourcePropertyViewModel propertyViewModel, IStringLocalizer<Resources.Resources> loc, BrowserTimeProvider browserTimeProvider)
    {
        _propertyViewModel = propertyViewModel;
        _loc = loc;
        _browserTimeProvider = browserTimeProvider;

        // Known and unknown properties are displayed together. Avoid any duplicate keys.
        _key = propertyViewModel.KnownProperty != null ? propertyViewModel.KnownProperty.Key : $"unknown-{propertyViewModel.Name}";

        _tooltip = new(() => propertyViewModel.Value.HasStringValue ? propertyViewModel.Value.StringValue : propertyViewModel.Value.ToString());

        _displayValue = new(() =>
        {
            var value = propertyViewModel.Value is { HasStringValue: true, StringValue: var stringValue }
                ? stringValue
                // Complex values such as arrays and objects will be output as JSON.
                // Consider how complex values are rendered in the future.
                : propertyViewModel.Value.ToString();

            if (propertyViewModel.Name == KnownProperties.Container.Id)
            {
                // Container images have a short ID of 12 characters
                if (value.Length > 12)
                {
                    value = value[..12];
                }
            }
            else
            {
                // Dates are returned as ISO 8601 text. Try to parse. If successful, format with the current culture.
                if (DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    value = FormatHelpers.FormatDateTime(_browserTimeProvider, date, cultureInfo: CultureInfo.CurrentCulture);
                }
            }

            return value;
        });
    }

    public bool MatchesFilter(string filter) =>
        _propertyViewModel.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
        ToolTip.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}

[DebuggerDisplay("Name = {Name}, Value = {Value}, IsValueSensitive = {IsValueSensitive}, IsValueMasked = {IsValueMasked}")]
public sealed class ResourcePropertyViewModel
{
    public string Name { get; }
    public Value Value { get; }
    public KnownProperty? KnownProperty { get; }
    public bool IsValueSensitive { get; }
    public bool IsValueMasked { get; set; }
    public int Priority { get; }

    public ResourcePropertyViewModel(string name, Value value, bool isValueSensitive, KnownProperty? knownProperty, int priority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        Value = value;
        IsValueSensitive = isValueSensitive;
        KnownProperty = knownProperty;
        Priority = priority;
        IsValueMasked = isValueSensitive;
    }
}

public sealed record KnownProperty(string Key, Func<IStringLocalizer<Resources.Resources>, string> GetDisplayName);

[DebuggerDisplay("EndpointName = {EndpointName}, Url = {Url}, IsInternal = {IsInternal}")]
public sealed class UrlViewModel
{
    public string? EndpointName { get; }
    public Uri Url { get; }
    public bool IsInternal { get; }
    public bool IsInactive { get; }
    public UrlDisplayPropertiesViewModel DisplayProperties { get; }

    public UrlViewModel(string? endpointName, Uri url, bool isInternal, bool isInactive, UrlDisplayPropertiesViewModel displayProperties)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(displayProperties);

        EndpointName = endpointName;
        Url = url;
        IsInternal = isInternal;
        DisplayProperties = displayProperties;
        IsInactive = isInactive;
    }
}

public record UrlDisplayPropertiesViewModel(string DisplayName, int SortOrder)
{
    public static readonly UrlDisplayPropertiesViewModel Empty = new(string.Empty, 0);
}

public sealed record class VolumeViewModel(int index, string Source, string Target, string MountType, bool IsReadOnly) : IPropertyGridItem
{
    string IPropertyGridItem.Name => Source;
    string? IPropertyGridItem.Value => Target;

    // Source could be empty for an anomymous volume so it can't be used as a key.
    // Because there is no good key in data, use index of the volume in results.
    object IPropertyGridItem.Key => index;

    public bool MatchesFilter(string filter) =>
        Source?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true ||
        Target?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true;
}

public sealed record class HealthReportViewModel(string Name, HealthStatus? HealthStatus, string? Description, string? ExceptionText)
{
    /// <summary>
    /// The timestamp when this health check was last executed, or <see langword="null"/> if not available.
    /// </summary>
    public DateTime? LastRunAtTimeStamp { get; init; }
    private readonly string? _humanizedHealthStatus = HealthStatus?.Humanize();

    public string? DisplayedDescription
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Description))
            {
                return Description;
            }

            if (!string.IsNullOrWhiteSpace(ExceptionText))
            {
                var newLineIndex = ExceptionText.IndexOfAny(['\n', '\r']);
                return newLineIndex > 0 ? ExceptionText[..newLineIndex] : ExceptionText;
            }

            return null;
        }
    }

    public bool MatchesFilter(string filter)
    {
        return
            Name?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true ||
            Description?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true ||
            _humanizedHealthStatus?.Contains(filter, StringComparison.OrdinalIgnoreCase) is true;
    }
}

[DebuggerDisplay("ResourceName = {ResourceName}, Type = {Type}")]
public sealed class RelationshipViewModel
{
    public string ResourceName { get; }
    public string Type { get; }

    public RelationshipViewModel(string resourceName, string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        ResourceName = resourceName;
        Type = type;
    }
}
