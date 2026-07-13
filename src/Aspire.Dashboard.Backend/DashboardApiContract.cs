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
