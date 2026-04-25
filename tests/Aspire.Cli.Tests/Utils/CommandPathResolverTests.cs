// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class CommandPathResolverTests
{
    [Theory]
    [InlineData("npm", "npm is not installed or not found in PATH. Please install Node.js and try again.")]
    [InlineData("npm.cmd", "npm is not installed or not found in PATH. Please install Node.js and try again.")]
    [InlineData("npx", "npx is not installed or not found in PATH. Please install Node.js and try again.")]
    [InlineData("npx.cmd", "npx is not installed or not found in PATH. Please install Node.js and try again.")]
    [InlineData("bun", "bun is not installed or not found in PATH. Please install Bun and try again.")]
    [InlineData("bun.cmd", "bun is not installed or not found in PATH. Please install Bun and try again.")]
    [InlineData("yarn", "yarn is not installed or not found in PATH. Please install Yarn and try again.")]
    [InlineData("pnpm", "pnpm is not installed or not found in PATH. Please install pnpm and try again.")]
    public void TryResolveCommand_WhenJavaScriptCommandIsMissing_ReturnsToolSpecificInstallMessage(string command, string expectedMessage)
    {
        static string? MissingCommandResolver(string _) => null;

        var success = CommandPathResolver.TryResolveCommand(command, MissingCommandResolver, out var resolvedCommand, out var errorMessage);

        Assert.False(success);
        Assert.Null(resolvedCommand);
        Assert.Equal(expectedMessage, errorMessage);
    }

    [Fact]
    public void TryResolveCommand_WhenCustomCommandIsMissing_ReturnsGenericMessage()
    {
        static string? MissingCommandResolver(string _) => null;

        var success = CommandPathResolver.TryResolveCommand("mytool", MissingCommandResolver, out var resolvedCommand, out var errorMessage);

        Assert.False(success);
        Assert.Null(resolvedCommand);
        Assert.Equal("Command 'mytool' not found. Please ensure it is installed and in your PATH.", errorMessage);
    }

    [Fact]
    public void TryResolveCommand_WhenCommandExists_ReturnsResolvedPath()
    {
        static string? Resolver(string command) => $"/test/bin/{command}";

        var success = CommandPathResolver.TryResolveCommand("npm", Resolver, out var resolvedCommand, out var errorMessage);

        Assert.True(success);
        Assert.Equal("/test/bin/npm", resolvedCommand);
        Assert.Null(errorMessage);
    }

    [Theory]
    [InlineData("npm", "https://nodejs.org/en/download")]
    [InlineData("npx", "https://nodejs.org/en/download")]
    [InlineData("bun", "https://bun.sh/docs/installation")]
    [InlineData("yarn", "https://yarnpkg.com/getting-started/install")]
    [InlineData("pnpm", "https://pnpm.io/installation")]
    public void GetInstallationLink_WhenJavaScriptCommandKnown_ReturnsExpectedLink(string command, string expectedLink)
    {
        Assert.Equal(expectedLink, CommandPathResolver.GetInstallationLink(command));
    }
}
