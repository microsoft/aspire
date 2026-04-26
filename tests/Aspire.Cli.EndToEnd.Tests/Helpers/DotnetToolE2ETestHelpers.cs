// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal static class DotnetToolE2ETestHelpers
{
    internal static CliInstallStrategy ResolveRequiredStrategy()
    {
        var strategy = TryResolveStrategy();
        if (strategy is not null)
        {
            return strategy;
        }

        const string message =
            "Dotnet tool E2E tests require one of: ASPIRE_E2E_DOTNET_TOOL_SOURCE, " +
            "ASPIRE_E2E_DOTNET_TOOL=true, or BUILT_NUGETS_PATH containing the Aspire.Cli tool nupkg.";

        if (CliE2ETestHelpers.IsRunningInCI)
        {
            Assert.Fail(message);
        }

        Assert.Skip(message);
        return null!;
    }

    private static CliInstallStrategy? TryResolveStrategy()
    {
        var source = Environment.GetEnvironmentVariable("ASPIRE_E2E_DOTNET_TOOL_SOURCE");
        if (!string.IsNullOrEmpty(source))
        {
            var version = Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION");
            return !string.IsNullOrEmpty(version)
                ? CliInstallStrategy.FromDotnetToolLocalSource(source, version)
                : CliInstallStrategy.FromDotnetToolLocalSource(source);
        }

        var dotnetTool = Environment.GetEnvironmentVariable("ASPIRE_E2E_DOTNET_TOOL");
        if (string.Equals(dotnetTool, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(dotnetTool, "1", StringComparison.OrdinalIgnoreCase))
        {
            return CliInstallStrategy.FromDotnetTool(Environment.GetEnvironmentVariable("ASPIRE_E2E_VERSION"));
        }

        var builtNugetsPath = Environment.GetEnvironmentVariable("BUILT_NUGETS_PATH");
        if (!string.IsNullOrEmpty(builtNugetsPath) && Directory.Exists(builtNugetsPath))
        {
            return CliInstallStrategy.FromDotnetToolLocalSource(builtNugetsPath);
        }

        return null;
    }
}
