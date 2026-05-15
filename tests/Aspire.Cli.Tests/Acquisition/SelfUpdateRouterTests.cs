// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Tests.Acquisition;

public class SelfUpdateRouterTests
{
    [Theory]
    // Script-route installs stay in-process — the CLI owns the binary swap.
    [InlineData("Script", "InProcess")]
    // Unknown sources fail closed unless the caller explicitly opts into
    // the UpdateCommand-layer --force override.
    [InlineData("Unknown", "Delegate")]
    // Every other route delegates — they're either pinned (PR), package-
    // manager-owned (winget / brew / dotnet-tool), or rebuilt-from-source
    // (localhive). An in-process binary swap would corrupt or demote them.
    [InlineData("Pr", "Delegate")]
    [InlineData("Winget", "Delegate")]
    [InlineData("Brew", "Delegate")]
    [InlineData("DotnetTool", "Delegate")]
    [InlineData("LocalHive", "Delegate")]
    public void GetAction_ReturnsExpectedAction(string sourceName, string expectedActionName)
    {
        var source = Enum.Parse<InstallSource>(sourceName);
        var expected = Enum.Parse<SelfUpdateAction>(expectedActionName);

        Assert.Equal(expected, SelfUpdateRouter.GetAction(source));
    }
}
