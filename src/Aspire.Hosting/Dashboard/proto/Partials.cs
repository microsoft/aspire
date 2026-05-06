// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dashboard;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.DashboardService.Proto.V1;

partial class Resource
{
    public static Resource FromSnapshot(ResourceSnapshot snapshot)
    {
        var resource = new Resource
        {
            Name = snapshot.Name,
            ResourceType = snapshot.ResourceType,
            DisplayName = snapshot.DisplayName,
            Uid = snapshot.Uid,
            State = snapshot.State ?? "",
            StateStyle = snapshot.StateStyle ?? "",
            IsHidden = snapshot.IsHidden,
            SupportsDetailedTelemetry = snapshot.SupportsDetailedTelemetry
        };

        if (snapshot.IconName is not null)
        {
            resource.IconName = snapshot.IconName;
        }

        if (snapshot.IconVariant is not null)
        {
            resource.IconVariant = MapIconVariant(snapshot.IconVariant);
        }

        if (snapshot.CreationTimeStamp.HasValue)
        {
            resource.CreatedAt = Timestamp.FromDateTime(snapshot.CreationTimeStamp.Value.ToUniversalTime());
        }

        if (snapshot.StartTimeStamp.HasValue)
        {
            resource.StartedAt = Timestamp.FromDateTime(snapshot.StartTimeStamp.Value.ToUniversalTime());
        }

        if (snapshot.StopTimeStamp.HasValue)
        {
            resource.StoppedAt = Timestamp.FromDateTime(snapshot.StopTimeStamp.Value.ToUniversalTime());
        }

        foreach (var env in snapshot.Environment)
        {
            resource.Environment.Add(new EnvironmentVariable { Name = env.Name, Value = env.Value ?? "", IsFromSpec = env.IsFromSpec });
        }

        foreach (var urlSnapshot in snapshot.Urls)
        {
            var url = new Url { EndpointName = urlSnapshot.Name ?? "", FullUrl = urlSnapshot.Url, IsInternal = urlSnapshot.IsInternal, IsInactive = urlSnapshot.IsInactive };
            var displayProperties = new UrlDisplayProperties();
            if (urlSnapshot.DisplayProperties?.DisplayName is not null)
            {
                displayProperties.DisplayName = urlSnapshot.DisplayProperties.DisplayName;
            }

            if (urlSnapshot.DisplayProperties?.SortOrder is not null)
            {
                displayProperties.SortOrder = urlSnapshot.DisplayProperties.SortOrder;
            }

            url.DisplayProperties = displayProperties;
            resource.Urls.Add(url);
        }

        foreach (var relationship in snapshot.Relationships)
        {
            resource.Relationships.Add(new ResourceRelationship
            {
                ResourceName = relationship.ResourceName,
                Type = relationship.Type
            });
        }

        foreach (var property in snapshot.Properties)
        {
            resource.Properties.Add(new ResourceProperty { Name = property.Name, Value = property.Value, IsSensitive = property.IsSensitive });
        }

        foreach (var volume in snapshot.Volumes)
        {
            resource.Volumes.Add(new Volume
            {
                Source = volume.Source ?? string.Empty,
                Target = volume.Target,
                MountType = volume.MountType,
                IsReadOnly = volume.IsReadOnly
            });
        }

        foreach (var command in snapshot.Commands.Where(command => command.Visibility.HasFlag(Hosting.ApplicationModel.ResourceCommandVisibility.Dashboard)))
        {
#pragma warning disable CS0612, CS0618 // Parameter is obsolete but still flowed for compatibility.
            var resourceCommand = new ResourceCommand { Name = command.Name, DisplayName = command.DisplayName, DisplayDescription = command.DisplayDescription ?? string.Empty, Parameter = ResourceSnapshot.ConvertToValue(command.Parameter), ConfirmationMessage = command.ConfirmationMessage ?? string.Empty, IconName = command.IconName ?? string.Empty, IconVariant = MapIconVariant(command.IconVariant), IsHighlighted = command.IsHighlighted, State = MapCommandState(command.State) };
#pragma warning restore CS0612, CS0618
            if (command.Arguments is { Count: > 0 } arguments)
            {
                resourceCommand.ArgumentInputs.AddRange(arguments.Select(input => Aspire.Hosting.Dashboard.DashboardService.CreateInteractionInputDto(input)));
            }
            resource.Commands.Add(resourceCommand);
        }

        foreach (var report in snapshot.HealthReports)
        {
            var healthReport = new HealthReport { Key = report.Name, Description = report.Description ?? "", Exception = report.ExceptionText ?? "" };

            if (report.Status is not null)
            {
                healthReport.Status = MapHealthStatus(report.Status.Value);
            }

            if (report.LastRunAt.HasValue)
            {
                healthReport.LastRunAt = Timestamp.FromDateTime(report.LastRunAt.Value.ToUniversalTime());
            }

            resource.HealthReports.Add(healthReport);
        }

        return resource;

        static HealthStatus MapHealthStatus(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus healthStatus)
        {
            return healthStatus switch
            {
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy => HealthStatus.Healthy,
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded => HealthStatus.Degraded,
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy => HealthStatus.Unhealthy,
                _ => throw new InvalidOperationException("Unknown health status: " + healthStatus),
            };
        }
    }

    private static IconVariant MapIconVariant(Hosting.ApplicationModel.IconVariant? iconVariant)
    {
        return iconVariant switch
        {
            Hosting.ApplicationModel.IconVariant.Regular => IconVariant.Regular,
            Hosting.ApplicationModel.IconVariant.Filled => IconVariant.Filled,
            null => IconVariant.Regular,
            _ => throw new InvalidOperationException("Unexpected icon variant: " + iconVariant)
        };
    }

    private static ResourceCommandState MapCommandState(Hosting.ApplicationModel.ResourceCommandState state)
    {
        return state switch
        {
            Hosting.ApplicationModel.ResourceCommandState.Enabled => ResourceCommandState.Enabled,
            Hosting.ApplicationModel.ResourceCommandState.Disabled => ResourceCommandState.Disabled,
            Hosting.ApplicationModel.ResourceCommandState.Hidden => ResourceCommandState.Hidden,
            _ => throw new InvalidOperationException("Unexpected state: " + state)
        };
    }
}
