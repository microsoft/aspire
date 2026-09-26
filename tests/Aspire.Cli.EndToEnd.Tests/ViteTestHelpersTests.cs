// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class ViteTestHelpersTests
{
    [Theory]
    [InlineData("viteapp", "'viteapp'")]
    [InlineData(".", "'.'")]
    [InlineData("vite app", "'vite app'")]
    [InlineData("vite'app", "'vite'\"'\"'app'")]
    public void GetCreateCommand_UsesPinnedManifestVersion(string projectDirectory, string quotedDirectory)
    {
        using var stream = typeof(ViteTestHelpers).Assembly.GetManifestResourceStream("Aspire.Cli.EndToEnd.Tests.package.json");
        Assert.NotNull(stream);
        using var manifest = JsonDocument.Parse(stream);
        var version = manifest.RootElement.GetProperty("devDependencies").GetProperty("create-vite").GetString();
        Assert.NotNull(version);
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);

        var command = ViteTestHelpers.GetCreateCommand(projectDirectory);

        Assert.Equal($"npm create -y 'vite@{version}' {quotedDirectory} -- --template vanilla-ts --no-interactive", command);
    }
}
