// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.DotnetWatch;

namespace Aspire.Cli.DotnetWatch;

/// <summary>
/// The environment <c>aspire run --watch</c> projects into the process that hosts Aspire.Hosting. Aspire.Hosting.Dotnet
/// does the service watching with its bundled watch host; the CLI owns AppHost generations.
/// </summary>
internal static class DotnetWatchRunEnvironment
{
    // Must match RunConfiguration.WatchEnabled binding (AppHost:Run:WatchEnabled) in Aspire.Hosting.
    public const string WatchEnabledVariable = "AppHost__Run__WatchEnabled";

    public static void Apply(IDictionary<string, string> environment)
    {
        environment[WatchEnabledVariable] = "true";
        environment[DotnetWatchOwnership.OwnerEnvironmentVariable] = DotnetWatchOwnership.CliOwner;
        ClearHostGeneration(environment);
    }

    internal static void Disable(IDictionary<string, string> environment)
    {
        // Empty overrides, rather than removals, fence inherited private state in ordinary,
        // publish, guest, and discovery processes launched from inside a watch run.
        environment[WatchEnabledVariable] = "false";
        ClearPrivate(environment);
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
