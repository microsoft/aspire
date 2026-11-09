// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

internal static class ResourceWatch
{
    internal const string ConfigurationKey = "AppHost:Run:WatchEnabled";
    internal const string SuppressionVariable = DotnetWatch.DotnetWatchOwnership.ResourceWatchSuppressionEnvironmentVariable;
    internal const string NoBuild = "no-build";
    internal const string Discovery = "discovery";
    internal const string Native = "native";

    internal const string Capability = "resource-watch-status.v1";
    internal const string NoticeCategory = "Aspire.Hosting.ResourceWatch";
    internal const int InvalidConfigurationEventId = 7190;

    internal const string StateProperty = "resource.watch.state";
    internal const string ReasonProperty = "resource.watch.reason";
    internal const string LogResourceProperty = "resource.watch.log-resource";

    internal const string Pending = "pending";
    internal const string Watching = "watching";
    internal const string BuildFailed = "build-failed";
    internal const string Excluded = "excluded";
    internal const string Failed = "failed";
}
