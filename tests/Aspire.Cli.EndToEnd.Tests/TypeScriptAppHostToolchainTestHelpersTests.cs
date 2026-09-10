// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class TypeScriptAppHostToolchainTestHelpersTests
{
    [Theory]
    [InlineData("npm", "npm install")]
    [InlineData("bun", "bun install")]
    [InlineData("yarn", "yarn install")]
    [InlineData("pnpm", "pnpm install")]
    [InlineData("deno", "deno install --minimum-dependency-age=0")]
    [InlineData("DENO", "deno install --minimum-dependency-age=0")]
    public void GetInstallCommand_ReturnsToolchainCommand(string toolchain, string expectedCommand)
    {
        var installCommand = TypeScriptAppHostToolchainTestHelpers.GetInstallCommand(toolchain);

        Assert.Equal(expectedCommand, installCommand);
    }

    [Fact]
    public void GetTypeCheckCommand_WhenToolchainIsDeno_EnablesSloppyImports()
    {
        var typeCheckCommand = TypeScriptAppHostToolchainTestHelpers.GetTypeCheckCommand("deno", "tsconfig.apphost.json");

        Assert.Equal("deno check --unstable-sloppy-imports apphost.mts", typeCheckCommand);
    }

    [Fact]
    public void GetWatchModeReadyText_WhenToolchainIsDeno_ReturnsTypeScriptCompilerWatchText()
    {
        var watchModeReadyText = TypeScriptAppHostToolchainTestHelpers.GetWatchModeReadyText("deno");

        Assert.Equal("Watching for file changes.", watchModeReadyText);
    }
}
