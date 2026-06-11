// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Tests.Shared.DashboardModel;

public static class ModelTestHelpers
{
    public static ResourceViewModel CreateResource(
        string? resourceName = null,
        KnownResourceState? state = null,
        string? displayName = null,
        ImmutableArray<UrlViewModel>? urls = null,
        Dictionary<string, ResourcePropertyViewModel>? properties = null,
        ImmutableArray<EnvironmentVariableViewModel>? environment = null,
        string? resourceType = null,
        string? stateStyle = null,
        HealthStatus? reportHealthStatus = null,
        bool createNullHealthReport = false,
        ImmutableArray<CommandViewModel>? commands = null,
        ImmutableArray<RelationshipViewModel>? relationships = null,
        bool hidden = false,
        string? iconName = null,
        IconVariant? iconVariant = null,
        ImmutableArray<VolumeViewModel>? volumes = null,
        ImmutableArray<HealthReportViewModel>? healthReports = null)
    {
        return new ResourceViewModel
        {
            Name = resourceName ?? "Name!",
            ResourceType = resourceType ?? KnownResourceTypes.Container,
            DisplayName = displayName ?? resourceName ?? "Display name!",
            Uid = Guid.NewGuid().ToString(),
            ReplicaIndex = 0,
            CreationTimeStamp = DateTime.UtcNow,
            StartTimeStamp = DateTime.UtcNow,
            StopTimeStamp = DateTime.UtcNow,
            Environment = environment ?? [],
            Urls = urls ?? [],
            Volumes = volumes ?? [],
            Properties = properties?.ToImmutableDictionary() ?? ImmutableDictionary<string, ResourcePropertyViewModel>.Empty,
            State = state?.ToString(),
            KnownState = state,
            StateStyle = stateStyle,
            HealthReports = healthReports ?? (reportHealthStatus is null && !createNullHealthReport ? [] : [new HealthReportViewModel("healthcheck", reportHealthStatus, null, null)]),
            Commands = commands ?? [],
            Relationships = relationships ?? [],
            IsHidden = hidden,
            IconName = iconName,
            IconVariant = iconVariant
        };
    }
}
