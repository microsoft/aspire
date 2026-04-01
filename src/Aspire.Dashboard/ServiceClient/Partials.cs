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
            return new()
            {
                Name = ValidateNotNull(Name),
                ResourceType = ValidateNotNull(ResourceType),
                DisplayName = ValidateNotNull(DisplayName),
                Uid = ValidateNotNull(Uid),
                ReplicaIndex = replicaIndex,
                CreationTimeStamp = ValidateNotNull(CreatedAt).ToDateTime(),
                StartTimeStamp = StartedAt?.ToDateTime(),
                StopTimeStamp = StoppedAt?.ToDateTime(),
                Properties = CreatePropertyViewModels(Properties, knownPropertyLookup, logger),
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
                .Select(c => new CommandViewModel(c.Name, MapState(c.State), c.DisplayName, c.DisplayDescription, c.ConfirmationMessage, c.Parameter, c.IsHighlighted, c.IconName, MapIconVariant(c.IconVariant)))
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

    private ImmutableDictionary<string, ResourcePropertyViewModel> CreatePropertyViewModels(RepeatedField<ResourceProperty> properties, IKnownPropertyLookup knownPropertyLookup, ILogger logger)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ResourcePropertyViewModel>(StringComparers.ResourcePropertyName);

        foreach (var property in properties)
        {
            var (priority, knownProperty) = knownPropertyLookup.FindProperty(ResourceType, property.Name);
            var propertyViewModel = new ResourcePropertyViewModel(
                name: ValidateNotNull(property.Name),
                value: ValidateNotNull(property.Value),
                isValueSensitive: property.IsSensitive,
                knownProperty: knownProperty,
                priority: priority)
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
        return new ResourceCommandResponseViewModel()
        {
            ErrorMessage = ErrorMessage,
            Kind = (Dashboard.Model.ResourceCommandResponseKind)Kind,
            Result = HasResult ? Result : null,
            ResultFormat = ResultFormat switch
            {
                CommandResultFormat.Text => Dashboard.Model.CommandResultFormat.Text,
                CommandResultFormat.Json => Dashboard.Model.CommandResultFormat.Json,
                _ => null
            }
        };
    }
}
