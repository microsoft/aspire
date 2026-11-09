// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;
using Aspire.Shared.DotnetWatch;

namespace Aspire.Cli.DotnetWatch;

/// <summary>
/// Launcher constraints and private AppHost-generation ownership; never overrides AppHost resource-watch defaults.
/// </summary>
internal static class DotnetWatchRunEnvironment
{
    // Must match RunConfiguration.WatchEnabled binding (AppHost:Run:WatchEnabled) in Aspire.Hosting.
    public const string WatchEnabledVariable = "AppHost__Run__WatchEnabled";

    public static void Apply(IDictionary<string, string> environment) =>
        PrepareRun(environment, noBuild: false, appHostWatching: true);

    internal static void PrepareRun(IDictionary<string, string> environment, bool noBuild, bool appHostWatching)
    {
        ClearPrivate(environment);
        environment[DotnetWatchOwnership.OwnerEnvironmentVariable] = appHostWatching ? DotnetWatchOwnership.CliOwner : "";
        environment[ResourceWatch.SuppressionVariable] = noBuild ? ResourceWatch.NoBuild : "";
    }

    internal static void Disable(IDictionary<string, string> environment, string reason = ResourceWatch.Discovery)
    {
        // Empty overrides, rather than removals, fence inherited private state in ordinary,
        // publish, guest, and discovery processes launched from inside a watch run.
        ClearPrivate(environment);
        environment[ResourceWatch.SuppressionVariable] = reason;
    }

    internal static void ClearPrivate(IDictionary<string, string> environment)
    {
        foreach (var name in DotnetWatchOwnership.PrivateEnvironmentVariables)
        {
            environment[name] = "";
        }
    }

    internal static void ClearHostGeneration(IDictionary<string, string> environment)
    {
        environment[DotnetWatchOwnership.AppHostControlEnvironmentVariable] = "";
        environment[DotnetWatchOwnership.AppHostSessionEnvironmentVariable] = "";
    }
}
