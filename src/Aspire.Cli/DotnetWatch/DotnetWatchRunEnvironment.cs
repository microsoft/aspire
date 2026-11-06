// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.DotnetWatch;

/// <summary>
/// The environment <c>aspire run --watch</c> projects into the process that hosts Aspire.Hosting. Aspire.Hosting.Dotnet
/// does the rest with the watch host that ships in its package; the CLI takes no part in watching services.
/// </summary>
internal static class DotnetWatchRunEnvironment
{
    // Must match RunConfiguration.WatchEnabled binding (AppHost:Run:WatchEnabled) in Aspire.Hosting.
    public const string WatchEnabledVariable = "AppHost__Run__WatchEnabled";

    public static void Apply(IDictionary<string, string> environment) => environment[WatchEnabledVariable] = "true";
}
