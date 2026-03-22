// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class CommandPathResolverTests
{
    [Theory]
    [InlineData("npm")]
    [InlineData("npm.cmd")]
    [InlineData("npx")]
    [InlineData("npx.cmd")]
    public void TryResolveCommand_WhenNodeCommandIsMissing_ReturnsNodeInstallMessage(string command)
    {
        static string? MissingCommandResolver(string _) => null;

        var success = CommandPathResolver.TryResolveCommand(command, MissingCommandResolver, out var resolvedCommand, out var errorMessage);

        Assert.False(success);
        Assert.Null(resolvedCommand);
        Assert.Equal($"{Path.GetFileNameWithoutExtension(command)} is not installed or not found in PATH. Please install Node.js and try again.", errorMessage);
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
}
