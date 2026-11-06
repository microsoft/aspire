// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Utils;
using Aspire.Shared;

namespace Aspire.Cli.Watch;

internal static class DotnetWatchLaunchEnvironment
{
    internal static string WatchEnabledVariable { get; } = DotnetWatchConfiguration.WatchEnabled.Replace(":", "__", StringComparison.Ordinal);

    internal static void Apply(IDictionary<string, string> environment, CliSelfInvocation invocation)
    {
        environment[WatchEnabledVariable] = "true";
        environment[DotnetWatchConfiguration.LaunchOwner] = DotnetWatchConfiguration.OwnerValue;
        environment[DotnetWatchConfiguration.CliExecutable] = invocation.ExecutablePath;
        environment[DotnetWatchConfiguration.CliArguments] = JsonSerializer.Serialize(invocation.Arguments, DotnetWatchJsonContext.Default.StringArray);
        ClearHostGeneration(environment);
    }

    internal static void Disable(IDictionary<string, string> environment)
    {
        // Absent keys do not clear inherited state in the process factory. Discovery servers and
        // ordinary launches inside a watch run must not acquire its watcher or generation.
        environment[WatchEnabledVariable] = "false";
        environment[DotnetWatchConfiguration.LaunchOwner] = "";
        environment[DotnetWatchConfiguration.CliExecutable] = "";
        environment[DotnetWatchConfiguration.CliArguments] = "";
        environment[DotnetWatchConfiguration.ToolPath] = "";
        environment[DotnetWatchConfiguration.ToolVersion] = "";
        environment[DotnetWatchConfiguration.ToolRuntimeMajor] = "";
        ClearHostGeneration(environment);
    }

    internal static void ClearHostGeneration(IDictionary<string, string> environment)
    {
        environment[DotnetWatchConfiguration.HostControlPipe] = "";
        environment[DotnetWatchConfiguration.HostSession] = "";
    }
}
