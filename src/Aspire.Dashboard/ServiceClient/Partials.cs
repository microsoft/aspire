// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Aspire.Dashboard.Model;
using FluentUIIconVariant = Microsoft.FluentUI.AspNetCore.Components.IconVariant;
using Aspire.Dashboard.Resources;
using Aspire.Hosting;
using Google.Protobuf.Collections;

namespace Aspire.DashboardService.Proto.V1;

partial class Resource
{
    /// <summary>
    /// Converts this gRPC message object to a view model for use in the dashboard UI.
    /// </summary>
    public ResourceViewModel ToViewModel(int replicaIndex, IKnownPropertyLookup knownPropertyLookup, ILogger logger)
    {
        try
        {
            var resourceType = ValidateNotNull(ResourceType);

            return new()
            {
                Name = ValidateNotNull(Name),
                ResourceType = resourceType,
                DisplayName = ValidateNotNull(DisplayName),
                Uid = ValidateNotNull(Uid),
                ReplicaIndex = replicaIndex,
                CreationTimeStamp = ValidateNotNull(CreatedAt).ToDateTime(),
                StartTimeStamp = StartedAt?.ToDateTime(),
                StopTimeStamp = StoppedAt?.ToDateTime(),
                Properties = CreatePropertyViewModels(resourceType, Properties, knownPropertyLookup, logger),
                Environment = GetEnvironment(),
                Urls = GetUrls(),
                Volumes = GetVolumes(),
                Relationships = GetRelationships(),
                State = HasState ? State : null,
                KnownState = HasState ? Enum.TryParse(State, out KnownResourceState knownState) ? knownState : null : null,
                StateStyle = HasStateStyle ? StateStyle : null,
                Commands = GetCommands(),
                HealthReports = HealthReports.Select(ToHealthReportViewModel).OrderBy(vm => vm.Name).ToImmutableArray(),
                IsHidden = IsHidden,
                SupportsDetailedTelemetry = SupportsDetailedTelemetry,
                IconName = HasIconName ? IconName : null,
                IconVariant = HasIconVariant ? MapResourceIconVariant(IconVariant) : null
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($@"Error converting resource ""{Name}"" to {nameof(ResourceViewModel)}.", ex);
        }

        HealthReportViewModel ToHealthReportViewModel(HealthReport healthReport)
        {
            return new HealthReportViewModel(
                healthReport.Key, 
                healthReport.HasStatus ? MapHealthStatus(healthReport.Status) : null, 
                healthReport.Description, 
                healthReport.Exception)
            {
                LastRunAtTimeStamp = healthReport.LastRunAt?.ToDateTime()
            };
        }

        Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus MapHealthStatus(HealthStatus healthStatus)
        {
            return healthStatus switch
            {
                HealthStatus.Healthy => Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,
                HealthStatus.Degraded => Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
                HealthStatus.Unhealthy => Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                _ => throw new InvalidOperationException("Unknown health status: " + healthStatus),
            };
        }

        ImmutableArray<EnvironmentVariableViewModel> GetEnvironment()
        {
            return Environment
                .Select(e => new EnvironmentVariableViewModel(e.Name, e.Value, e.IsFromSpec))
                .ToImmutableArray();
        }

        ImmutableArray<RelationshipViewModel> GetRelationships()
        {
            return Relationships
                .Select(r => new RelationshipViewModel(r.ResourceName, r.Type))
                .ToImmutableArray();
        }

        ImmutableArray<UrlViewModel> GetUrls()
        {
            static string TranslateKnownUrlName(Url url)
            {
                return (url.EndpointName, url.DisplayProperties.DisplayName) switch
                {
                    (KnownUrls.DataExplorer.EndpointName, KnownUrls.DataExplorer.DisplayText) => KnownUrlsDisplay.DataExplorer,
                    _ => url.DisplayProperties.DisplayName
                };
            }

            // Filter out bad urls
            return (from u in Urls
                    let parsedUri = Uri.TryCreate(u.FullUrl, UriKind.Absolute, out var uri) ? uri : null
                    where parsedUri != null
                    select new UrlViewModel(u.EndpointName, parsedUri, u.IsInternal, u.IsInactive, new UrlDisplayPropertiesViewModel(TranslateKnownUrlName(u), u.DisplayProperties.SortOrder)))
                .ToImmutableArray();
        }

        ImmutableArray<VolumeViewModel> GetVolumes()
        {
            return Volumes
                .Select((v, i) => new VolumeViewModel(i, v.Source, v.Target, v.MountType, v.IsReadOnly))
                .ToImmutableArray();
        }

        ImmutableArray<CommandViewModel> GetCommands()
        {
            return Commands
                .Select(c => new CommandViewModel(c.Name, MapState(c.State), c.DisplayName, c.DisplayDescription, c.ConfirmationMessage, c.ArgumentInputs.ToImmutableArray(), c.IsHighlighted, c.IconName, MapIconVariant(c.IconVariant)))
                .ToImmutableArray();

            static CommandViewModelState MapState(ResourceCommandState state)
            {
                return state switch
                {
                    ResourceCommandState.Enabled => CommandViewModelState.Enabled,
                    ResourceCommandState.Disabled => CommandViewModelState.Disabled,
                    ResourceCommandState.Hidden => CommandViewModelState.Hidden,
                    _ => throw new InvalidOperationException("Unknown state: " + state),
                };
            }

            static FluentUIIconVariant MapIconVariant(IconVariant iconVariant)
            {
                return iconVariant switch
                {
                    IconVariant.Regular => FluentUIIconVariant.Regular,
                    IconVariant.Filled => FluentUIIconVariant.Filled,
                    _ => throw new InvalidOperationException("Unknown icon variant: " + iconVariant),
                };
            }
        }

        static FluentUIIconVariant MapResourceIconVariant(IconVariant iconVariant)
        {
            return iconVariant switch
            {
                IconVariant.Regular => FluentUIIconVariant.Regular,
                IconVariant.Filled => FluentUIIconVariant.Filled,
                _ => throw new InvalidOperationException("Unknown icon variant: " + iconVariant),
            };
        }
    }

    private ImmutableDictionary<string, ResourcePropertyViewModel> CreatePropertyViewModels(string resourceType, RepeatedField<ResourceProperty> properties, IKnownPropertyLookup knownPropertyLookup, ILogger logger)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ResourcePropertyViewModel>(StringComparers.ResourcePropertyName);
        var useLegacyMetadata = ShouldUseLegacyResourcePropertyMetadata(resourceType, properties);

        foreach (var property in properties)
        {
            var (sortOrder, knownProperty) = knownPropertyLookup.FindProperty(property.Name);
            var legacyMetadata = useLegacyMetadata ? LegacyResourcePropertyMetadata.Get(resourceType, property.Name) : null;

            var propertyViewModel = new ResourcePropertyViewModel(
                name: ValidateNotNull(property.Name),
                value: ValidateNotNull(property.Value),
                isValueSensitive: property.IsSensitive,
                knownProperty: knownProperty ?? legacyMetadata?.KnownProperty,
                sortOrder: GetDisplaySortOrder(property, knownProperty, legacyMetadata?.SortOrder, sortOrder),
                displayName: property.HasDisplayName ? property.DisplayName : null,
                isHighlighted: property.IsHighlighted)
            {
                IsValueMasked = property.IsSensitive
            };

            if (builder.ContainsKey(propertyViewModel.Name))
            {
                logger.LogWarning("Duplicate property '{PropertyName}' found in resource '{ResourceName}'.", propertyViewModel.Name, Name);
            }

            builder[propertyViewModel.Name] = propertyViewModel;
        }

        return builder.ToImmutable();
    }

    private static int GetDisplaySortOrder(ResourceProperty property, KnownProperty? knownProperty, int? legacySortOrder, int knownSortOrder)
    {
        if (legacySortOrder is { } legacyOrder)
        {
            // Legacy fallback metadata represents built-in producer-specific properties from
            // older resource servers, so treat its order as producer-local.
            return ToProducerDefinedDisplaySortOrder(legacyOrder);
        }

        if (knownProperty is not null)
        {
            // Generic dashboard-known properties keep their fixed dashboard sort order.
            return knownSortOrder;
        }

        // Unknown properties with producer metadata use producer-local ordering. Unknown
        // properties without metadata keep the default "sort last" order from the caller.
        return property.HasSortOrder ? ToProducerDefinedDisplaySortOrder(property.SortOrder) : knownSortOrder;
    }

    private static int ToProducerDefinedDisplaySortOrder(int producerSortOrder)
    {
        // Producers use local sort orders for their own resource-specific properties. The
        // dashboard normalizes those values after the generic dashboard-owned properties.
        var producerDefinedStart = KnownResourcePropertySortOrder.GetProducerDefinedStart();
        if (producerSortOrder <= 0)
        {
            return producerDefinedStart;
        }

        var sortOrder = producerDefinedStart + (long)producerSortOrder;
        return sortOrder > int.MaxValue ? int.MaxValue : (int)sortOrder;
    }

    private static bool ShouldUseLegacyResourcePropertyMetadata(string resourceType, RepeatedField<ResourceProperty> properties)
    {
        // Compatibility shim for dashboards connected to resource servers that predate
        // ResourceProperty.DisplayName/IsHighlighted/SortOrder. If any resource-specific
        // property already carries producer metadata, trust the producer and do not apply
        // dashboard fallback metadata to the rest of the resource.
        var hasLegacyResourceSpecificProperty = false;

        foreach (var property in properties)
        {
            if (LegacyResourcePropertyMetadata.Get(resourceType, property.Name) is null)
            {
                continue;
            }

            hasLegacyResourceSpecificProperty = true;

            if (property.HasDisplayName || property.IsHighlighted || property.HasSortOrder)
            {
                return false;
            }
        }

        return hasLegacyResourceSpecificProperty;
    }

    private T ValidateNotNull<T>(T value, [CallerArgumentExpression(nameof(value))] string? expression = null) where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException($"Message field '{expression}' on resource with name '{Name}' cannot be null.");
        }

        return value;
    }
}

partial class ResourceCommandResponse
{
    public ResourceCommandResponseViewModel ToViewModel()
    {
        // Map deprecated error_message to message for backward compatibility.
#pragma warning disable CS0612 // Type or member is obsolete
        var resolvedMessage = HasMessage ? Message : ErrorMessage;
#pragma warning restore CS0612 // Type or member is obsolete

        return new ResourceCommandResponseViewModel()
        {
            ErrorMessage = resolvedMessage,
            Message = resolvedMessage,
            Kind = (Dashboard.Model.ResourceCommandResponseKind)Kind,
            Result = Result is not null ? new ResourceCommandResultViewModel
            {
                Value = Result.Value,
                Format = Result.Format switch
                {
                    CommandResultFormat.Text => Dashboard.Model.CommandResultFormat.Text,
                    CommandResultFormat.Json => Dashboard.Model.CommandResultFormat.Json,
                    CommandResultFormat.Markdown => Dashboard.Model.CommandResultFormat.Markdown,
                    _ => Dashboard.Model.CommandResultFormat.Text
                },
                DisplayImmediately = Result.DisplayImmediately
            } : null
        };
    }
}
