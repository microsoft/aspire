// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Api;

internal static class DeckResourceMapper
{
    public static DeckResource Map(ResourceViewModel resource, IStringLocalizer<Resources.Resources> localizer)
    {
        return new DeckResource(
            Name: resource.Name,
            ResourceType: resource.ResourceType,
            DisplayName: resource.DisplayName,
            Uid: resource.Uid,
            State: resource.State,
            StateStyle: resource.StateStyle,
            Health: resource.HealthStatus?.ToString(),
            CreatedAt: resource.CreationTimeStamp,
            StartedAt: resource.StartTimeStamp,
            StoppedAt: resource.StopTimeStamp,
            Urls: resource.Urls.Select(MapUrl).ToArray(),
            Properties: resource.Properties.Values
                .OrderBy(property => property.SortOrder)
                .ThenBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => MapProperty(property, localizer))
                .ToArray(),
            Environment: resource.Environment.Select(MapEnvironmentVariable).ToArray(),
            HealthReports: resource.HealthReports.Select(MapHealthReport).ToArray(),
            Commands: resource.Commands.Select(MapCommand).ToArray(),
            Relationships: resource.Relationships.Select(MapRelationship).ToArray(),
            IsHidden: resource.IsResourceHidden(showHiddenResources: false),
            SupportsDetailedTelemetry: resource.SupportsDetailedTelemetry,
            IconName: resource.IconName);
    }

    private static DeckResourceUrl MapUrl(UrlViewModel url)
    {
        return new DeckResourceUrl(
            Name: url.EndpointName,
            Url: url.Url.ToString(),
            IsInternal: url.IsInternal,
            IsInactive: url.IsInactive,
            DisplayName: string.IsNullOrEmpty(url.DisplayProperties.DisplayName) ? null : url.DisplayProperties.DisplayName,
            SortOrder: url.DisplayProperties.SortOrder);
    }

    private static DeckResourceProperty MapProperty(ResourcePropertyViewModel property, IStringLocalizer<Resources.Resources> localizer)
    {
        var value = property.Value.TryConvertToString(out var stringValue)
            ? stringValue
            // Complex values such as arrays and objects use protobuf's JSON representation,
            // matching the existing resource details view.
            : property.Value.ToString();

        return new DeckResourceProperty(
            Name: property.Name,
            DisplayName: property.DisplayName ?? property.KnownProperty?.GetDisplayName(localizer),
            Value: value,
            IsSensitive: property.IsValueSensitive,
            IsHighlighted: property.IsHighlighted,
            SortOrder: property.SortOrder);
    }

    private static DeckEnvironmentVariable MapEnvironmentVariable(EnvironmentVariableViewModel environmentVariable)
    {
        return new DeckEnvironmentVariable(
            Name: environmentVariable.Name,
            Value: environmentVariable.Value,
            IsFromSpec: environmentVariable.FromSpec);
    }

    private static DeckHealthReport MapHealthReport(HealthReportViewModel healthReport)
    {
        return new DeckHealthReport(
            Status: healthReport.HealthStatus?.ToString(),
            Key: healthReport.Name,
            Description: healthReport.DisplayedDescription ?? string.Empty);
    }

    private static DeckResourceCommand MapCommand(CommandViewModel command)
    {
        return new DeckResourceCommand(
            Name: command.Name,
            DisplayName: command.GetDisplayName(),
            DisplayDescription: command.GetDisplayDescription(),
            ConfirmationMessage: string.IsNullOrEmpty(command.ConfirmationMessage) ? null : command.ConfirmationMessage,
            IconName: string.IsNullOrEmpty(command.IconName) ? null : command.IconName,
            IsHighlighted: command.IsHighlighted,
            State: command.State switch
            {
                CommandViewModelState.Enabled => "enabled",
                CommandViewModelState.Disabled => "disabled",
                CommandViewModelState.Hidden => "hidden",
                _ => throw new InvalidOperationException($"Unexpected {nameof(CommandViewModelState)} value: {command.State}.")
            });
    }

    private static DeckResourceRelationship MapRelationship(RelationshipViewModel relationship)
    {
        return new DeckResourceRelationship(
            ResourceName: relationship.ResourceName,
            Type: relationship.Type);
    }
}
