// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

internal static class DotnetWatchConfiguration
{
    internal const string LaunchOwner = "ASPIRE_DOTNET_WATCH_LAUNCH_OWNER";
    internal const string OwnerValue = "aspire";
    internal const string WatchEnabled = "AppHost:Run:WatchEnabled";
    internal const string ToolPath = "ASPIRE_DOTNET_WATCH_RESOLVED_TOOL";
    internal const string ToolVersion = "ASPIRE_DOTNET_WATCH_RESOLVED_VERSION";
    internal const string ToolRuntimeMajor = "ASPIRE_DOTNET_WATCH_RESOLVED_RUNTIME";
    internal const string CliExecutable = "ASPIRE_DOTNET_WATCH_CLI";
    internal const string CliArguments = "ASPIRE_DOTNET_WATCH_CLI_ARGUMENTS";
    internal const string ResourcePipe = "ASPIRE_DOTNET_WATCH_RESOURCE_PIPE";
    internal const string EntryPoint = "ASPIRE_DOTNET_WATCH_ENTRYPOINT";
    internal const string EnvironmentNames = "ASPIRE_DOTNET_WATCH_ENVIRONMENT_NAMES";
    internal const string LaunchProfile = "ASPIRE_DOTNET_WATCH_LAUNCH_PROFILE";
    internal const string ExcludeProfile = "ASPIRE_DOTNET_WATCH_EXCLUDE_PROFILE";
    internal const string HostControlPipe = "ASPIRE_DOTNET_WATCH_HOST_CONTROL";
    internal const string HostSession = "ASPIRE_DOTNET_WATCH_HOST_SESSION";
    internal const string ProxyCommand = "--internal-dotnet-watch-resource";
    internal const string ServerCommand = "--internal-dotnet-watch-server";
    internal const string ScopeCompletionPath = "ASPIRE_DOTNET_WATCH_SCOPE_COMPLETION";

    internal static bool IsCliOwned(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        configuration[LaunchOwner] == OwnerValue &&
        bool.TryParse(configuration[WatchEnabled], out var enabled) && enabled;
}
