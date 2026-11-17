// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

namespace Aspire.Shared;

/// <summary>Defines which AppHost runtime settings must not be inherited by DCP.</summary>
internal static class DcpEnvironmentVariables
{
    private static readonly string[] s_excluded =
    [
        KnownAspNetCoreConfigNames.Urls,
        "DOTNET_LAUNCH_PROFILE",
        KnownAspNetCoreConfigNames.Environment,
        KnownAspNetCoreConfigNames.DotNetEnvironment,
        KnownConfigNames.AspireLogLevel
    ];

    public static bool IsExcluded(string name) =>
        s_excluded.Contains(name, StringComparers.EnvironmentVariableName);
}
