// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Backend;

internal static class DashboardApiContract
{
    public const string Product = "Aspire.Dashboard";
    public const int CurrentVersion = 1;
    public const string DiscoveryPath = "/api/dashboard";
    public const string VersionOneBasePath = "/api/dashboard/v1";
    public const string ConfigurationCapability = "configuration";
    public const string ResourcesCapability = "resources";
}

internal sealed record DashboardApiDiscovery(
    string Product,
    DashboardApiVersion[] Versions);

internal sealed record DashboardApiVersion(
    int Version,
    string BasePath,
    string[] Capabilities);

internal sealed record DashboardConfiguration(
    string ApplicationName,
    string DashboardVersion,
    string RuntimeVersion);

internal sealed record DashboardResource(
    string Name,
    string ResourceType,
    string DisplayName,
    string Uid,
    string? State,
    string? StateStyle,
    string? Health,
    DateTime? CreatedAt,
    DateTime? StartedAt,
    DateTime? StoppedAt,
    DashboardResourceUrl[] Urls,
    DashboardResourceProperty[] Properties,
    DashboardEnvironmentVariable[] Environment,
    DashboardHealthReport[] HealthReports,
    DashboardResourceCommand[] Commands,
    DashboardResourceRelationship[] Relationships,
    bool IsHidden,
    bool SupportsDetailedTelemetry,
    string? IconName,
    string? IconVariant,
    bool HasTerminal,
    int? TerminalReplicaIndex);

internal sealed record DashboardResourceUrl(
    string? Name,
    string Url,
    bool IsInternal,
    bool IsInactive,
    string? DisplayName,
    int SortOrder);

internal sealed record DashboardResourceProperty(
    string Name,
    string? DisplayName,
    string Value,
    bool IsSensitive,
    bool IsHighlighted,
    int? SortOrder);

internal sealed record DashboardEnvironmentVariable(
    string Name,
    string? Value,
    bool IsFromSpec);

internal sealed record DashboardHealthReport(
    string? Status,
    string Key,
    string Description);

internal sealed record DashboardResourceCommand(
    string Name,
    string DisplayName,
    string? DisplayDescription,
    string? ConfirmationMessage,
    string? IconName,
    string IconVariant,
    bool IsHighlighted,
    string State);

internal sealed record DashboardResourceRelationship(
    string ResourceName,
    string Type);
