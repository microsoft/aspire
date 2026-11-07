// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.DotnetWatch;

/// <summary>
/// Identifies CLI-owned watch runs independently of the execution-service settings injected by the watcher.
/// </summary>
internal static class DotnetWatchOwnership
{
    internal const string OwnerEnvironmentVariable = "ASPIRE_DOTNET_WATCH_OWNER";
    internal const string CliOwner = "cli";
    internal const string AppHostControlEnvironmentVariable = "ASPIRE_DOTNET_WATCH_HOST_CONTROL";
    internal const string AppHostSessionEnvironmentVariable = "ASPIRE_DOTNET_WATCH_HOST_SESSION";

    internal static IReadOnlyList<string> PrivateEnvironmentVariables { get; } = Array.AsReadOnly(
    [
        OwnerEnvironmentVariable,
        AppHostControlEnvironmentVariable,
        AppHostSessionEnvironmentVariable
    ]);

    internal static bool IsCliOwned(string? owner) => string.Equals(owner, CliOwner, StringComparison.Ordinal);

    internal static bool IsPrivateEnvironmentVariable(string name)
        => PrivateEnvironmentVariables.Contains(name, StringComparer.OrdinalIgnoreCase);
}
