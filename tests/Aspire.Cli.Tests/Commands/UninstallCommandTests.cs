// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class UninstallCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task Uninstall_PrChannel_DeletesHiveAndDogfoodInstall()
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
        var result = command.Parse("uninstall --channel pr-123 --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(Directory.Exists(hivePath));
        Assert.False(Directory.Exists(dogfoodPath));
    }

    [Fact]
    public async Task Uninstall_MatchingGlobalChannel_DeletesGlobalChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-123", "packages"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var configurationService = provider.GetRequiredService<IConfigurationService>();
        await configurationService.SetConfigurationAsync("channel", "pr-123", isGlobal: true).DefaultTimeout();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("uninstall --channel pr-123 --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var globalSettingsPath = configurationService.GetSettingsFilePath(isGlobal: true);
        var globalSettings = JsonNode.Parse(await File.ReadAllTextAsync(globalSettingsPath).DefaultTimeout())?.AsObject();
        Assert.NotNull(globalSettings);
        Assert.False(globalSettings.ContainsKey("channel"));
    }

    [Fact]
    public async Task Uninstall_StableChannel_DoesNotRemoveSharedScriptInstallWithoutExplicitOption()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        var hivePath = Path.Combine(aspireHome, "hives", "stable");
        var binPath = Path.Combine(aspireHome, "bin");
        var binaryPath = Path.Combine(binPath, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        var bundlePath = Path.Combine(aspireHome, "bundle");
        var versionPath = Path.Combine(aspireHome, "versions", "v1");
        Directory.CreateDirectory(Path.Combine(hivePath, "packages"));
        Directory.CreateDirectory(binPath);
        Directory.CreateDirectory(bundlePath);
        Directory.CreateDirectory(versionPath);
        File.WriteAllText(binaryPath, string.Empty);

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("uninstall --channel stable --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(Directory.Exists(hivePath));
        Assert.True(File.Exists(binaryPath));
        Assert.True(Directory.Exists(bundlePath));
        Assert.True(Directory.Exists(versionPath));
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("--remove-shared-install", output);
    }

    [Fact]
    public async Task Uninstall_All_DoesNotRemoveStableLocalCustomOrSharedScriptInstall()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        var hivesRoot = Path.Combine(aspireHome, "hives");
        foreach (var hive in new[] { "pr-123", "staging", "daily", "stable", "local", "custom" })
        {
            Directory.CreateDirectory(Path.Combine(hivesRoot, hive, "packages"));
        }

        var binPath = Path.Combine(aspireHome, "bin");
        var binaryPath = Path.Combine(binPath, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        Directory.CreateDirectory(binPath);
        File.WriteAllText(binaryPath, string.Empty);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("uninstall --all --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(Directory.Exists(Path.Combine(hivesRoot, "pr-123")));
        Assert.False(Directory.Exists(Path.Combine(hivesRoot, "staging")));
        Assert.False(Directory.Exists(Path.Combine(hivesRoot, "daily")));
        Assert.True(Directory.Exists(Path.Combine(hivesRoot, "stable")));
        Assert.True(Directory.Exists(Path.Combine(hivesRoot, "local")));
        Assert.True(Directory.Exists(Path.Combine(hivesRoot, "custom")));
        Assert.True(File.Exists(binaryPath));
    }

    [Fact]
    public async Task Uninstall_All_DryRun_DoesNotReportMissingDogfoodInstall()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-123", "packages"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("uninstall --all --dry-run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains(Path.Combine(aspireHome, "hives", "pr-123"), output);
        Assert.DoesNotContain(Path.Combine(aspireHome, "dogfood", "pr-123"), output);
        // --dry-run must not actually delete anything.
        Assert.True(Directory.Exists(Path.Combine(aspireHome, "hives", "pr-123")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData("..")]
    [InlineData("a/../b")]
    public async Task Uninstall_RejectsChannelWithPathTraversal(string maliciousChannel)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        // Create a sibling directory that path-traversal would target. It must
        // still exist after the uninstall attempt — the service's channel
        // validator must reject the malicious input before any directory walk.
        var sibling = Path.Combine(workspace.WorkspaceRoot.FullName, "escape");
        Directory.CreateDirectory(sibling);
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"uninstall --channel \"{maliciousChannel}\" --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.True(Directory.Exists(sibling));
    }
}
