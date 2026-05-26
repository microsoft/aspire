// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class HivesCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task HivesList_ShowsHivesAndMatchingDogfoodInstalls()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-123", "packages"));
        Directory.CreateDirectory(Path.Combine(aspireHome, "dogfood", "pr-123", "bin"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("hives list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("pr-123", output);
        Assert.Contains(Path.Combine(aspireHome, "hives", "pr-123"), output);
        Assert.Contains("yes", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HivesDelete_PrHiveWithDogfoodInstall_RefusesWithoutForce()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        var hivePath = Path.Combine(aspireHome, "hives", "pr-123");
        var dogfoodPath = Path.Combine(aspireHome, "dogfood", "pr-123");
        Directory.CreateDirectory(Path.Combine(hivePath, "packages"));
        Directory.CreateDirectory(Path.Combine(dogfoodPath, "bin"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("hives delete pr-123 --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.True(Directory.Exists(hivePath));
        Assert.True(Directory.Exists(dogfoodPath));
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("aspire uninstall --channel pr-123", output);
    }

    [Fact]
    public async Task HivesDelete_PrHiveWithForce_DeletesOnlyHive()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        var hivePath = Path.Combine(aspireHome, "hives", "pr-123");
        var dogfoodPath = Path.Combine(aspireHome, "dogfood", "pr-123");
        Directory.CreateDirectory(Path.Combine(hivePath, "packages"));
        Directory.CreateDirectory(Path.Combine(dogfoodPath, "bin"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("hives delete pr-123 --yes --force");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(Directory.Exists(hivePath));
        Assert.True(Directory.Exists(dogfoodPath));
    }
}
