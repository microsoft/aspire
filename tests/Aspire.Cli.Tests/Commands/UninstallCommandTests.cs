// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Bundles;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class UninstallCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task UninstallCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("uninstall --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task UninstallCommand_NoArgs_Errors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("uninstall");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.MissingRequiredArgument, exitCode);
        Assert.NotEmpty(interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task UninstallCommand_BothPrAndPrefix_Errors()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse($"uninstall --pr 1234 {workspace.WorkspaceRoot.FullName}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.MissingRequiredArgument, exitCode);
    }

    [Fact]
    public async Task UninstallCommand_DryRun_PerformsNoIO_AndSkipsPrompt()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = Path.Combine(workspace.WorkspaceRoot.FullName, "install");
        WriteScriptRouteInstall(prefix);
        var aspireBinary = Path.Combine(prefix, "bin", OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        Assert.True(File.Exists(aspireBinary));

        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse($"uninstall {prefix} --dry-run");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(aspireBinary), "Dry-run must not delete anything");
        Assert.Empty(interactionService.BooleanPromptCalls);
    }

    [Fact]
    public async Task UninstallCommand_YesFlag_SkipsConfirmation_AndExecutesPlan()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = Path.Combine(workspace.WorkspaceRoot.FullName, "install");
        WriteScriptRouteInstall(prefix);
        var aspireBinary = Path.Combine(prefix, "bin", OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        Assert.True(File.Exists(aspireBinary));

        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse($"uninstall {prefix} --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(File.Exists(aspireBinary));
        Assert.Empty(interactionService.BooleanPromptCalls);
    }

    [Fact]
    public async Task UninstallCommand_PrTarget_UsesEnvironmentHome()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var _ = new EnvVarOverride("HOME", workspace.WorkspaceRoot.FullName);
        using var __ = new EnvVarOverride("USERPROFILE", workspace.WorkspaceRoot.FullName);

        var prefix = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "dogfood", "pr-1234");
        WritePrRouteInstall(prefix);
        Assert.True(Directory.Exists(prefix));

        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("uninstall --pr 1234 --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.False(Directory.Exists(prefix));
    }

    private static void WritePrRouteInstall(string prefix)
    {
        var binDir = Path.Combine(prefix, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire"), "stub");
        File.WriteAllText(
            Path.Combine(binDir, InstallSidecarReader.SidecarFileName),
            "{\"source\":\"pr\"}");
    }

    [Fact]
    public async Task UninstallCommand_UserDeclines_PerformsNoIO()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = Path.Combine(workspace.WorkspaceRoot.FullName, "install");
        WriteScriptRouteInstall(prefix);
        var aspireBinary = Path.Combine(prefix, "bin", OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");

        var interactionService = new TestInteractionService
        {
            ConfirmCallback = (_, _) => false,
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse($"uninstall {prefix}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.True(File.Exists(aspireBinary));
        Assert.Single(interactionService.BooleanPromptCalls);
    }

    [Fact]
    public async Task UninstallCommand_PackagerRoute_Refuses()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var prefix = Path.Combine(workspace.WorkspaceRoot.FullName, "winget-install");
        Directory.CreateDirectory(Path.Combine(prefix, "bin"));
        File.WriteAllText(
            Path.Combine(prefix, "bin", InstallSidecarReader.SidecarFileName),
            "{\"source\":\"winget\"}");

        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse($"uninstall {prefix} --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.InvalidCommand, exitCode);
        Assert.True(Directory.Exists(prefix), "Packager-owned install must not be touched");
        Assert.NotEmpty(interactionService.DisplayedErrors);
    }

    private static void WriteScriptRouteInstall(string prefix)
    {
        var binDir = Path.Combine(prefix, "bin");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire"), "stub");
        File.WriteAllText(
            Path.Combine(binDir, InstallSidecarReader.SidecarFileName),
            "{\"source\":\"script\"}");
        // Write a bundle marker + matching versions/<id>/ so the planner has
        // something to delete from the versions tree (exercising U-2 happy
        // path indirectly — the dedicated unit tests cover the edge cases).
        var version = "13.0.0|123|456";
        BundleService.WriteVersionMarker(prefix, version);
        var versionId = BundleService.ComputeVersionId(version);
        var versionDir = Path.Combine(prefix, BundleService.VersionsDirectoryName, versionId);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, "payload"), "stub");
    }
}
