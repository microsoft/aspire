// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Tests.Commands;

public class InstallsCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task InstallsSelf_Json_ReturnsOnlyRunningInstallation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/usr/local/bin/aspire",
                CanonicalPath = "/usr/local/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok,
            },
            [
                new InstallationInfo
                {
                    Path = "/peer/aspire",
                    CanonicalPath = "/peer/aspire",
                    Version = "13.1.0-preview",
                    Channel = "pr-1234",
                    Source = "pr",
                    PathStatus = InstallationPathStatus.Shadowed,
                    Status = InstallationInfoStatus.Ok,
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs --self --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var installations = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsArray();
        var row = Assert.Single(installations);
        Assert.Equal("/usr/local/bin/aspire", row!["path"]!.GetValue<string>());
        Assert.Equal("13.0.0", row["version"]!.GetValue<string>());
        Assert.Equal("stable", row["channel"]!.GetValue<string>());
        Assert.Equal("script", row["source"]!.GetValue<string>());
        Assert.Equal(InstallationPathStatus.Active, row["pathStatus"]!.GetValue<string>());
        Assert.Equal(InstallationInfoStatus.Ok, row["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task InstallsSelf_List_ReturnsOnlyRunningInstallationWithoutChecks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/usr/local/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Source = "script",
                PathStatus = "custom[red]status[/]",
                Status = InstallationInfoStatus.Ok,
            },
            [
                new InstallationInfo
                {
                    Path = "/peer/aspire",
                    Status = InstallationInfoStatus.Ok,
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs --self");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("/usr/local/bin/aspire", output, StringComparison.Ordinal);
        Assert.Contains("custom[red]status[/]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/peer/aspire", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Summary:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallsList_ShowsDiscoveredInstallsAndOrphanHives()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-17400", "packages"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, "bin", "aspire"),
                CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                Version = "13.2.0",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("script", output);
        Assert.Contains("stable", output);
        Assert.Contains("orphan-hive", output);
        Assert.Contains("pr-17400", output);
        Assert.Contains("no install found", output);
        Assert.Contains("Cleanup", output);
        Assert.DoesNotContain("│", output);
    }

    [Fact]
    public async Task InstallsList_FormatList_IsAcceptedAndUsesVerticalOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, "bin", "aspire"),
                CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                Version = "13.2.0",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list --format list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.Contains("Cleanup", output);
        Assert.DoesNotContain("│", output);
    }

    [Fact]
    public void InstallsList_FormatTable_IsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list --format table");

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task InstallsList_Json_IncludesDiscoveredInstallsBrokenRowsAndOrphanHives()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-17400", "packages"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, "bin", "aspire"),
                CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                Version = "13.2.0",
                Channel = "stable",
                Source = "script",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            },
            [
                new InstallationInfo
                {
                    Path = Path.Combine(aspireHome, "broken", "aspire"),
                    CanonicalPath = Path.Combine(aspireHome, "broken", "aspire"),
                    Channel = "daily",
                    Source = "script",
                    PathStatus = InstallationPathStatus.Shadowed,
                    Status = InstallationInfoStatus.Failed,
                    StatusReason = "peer probe failed"
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var json = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsArray();
        Assert.Collection(
            json,
            row =>
            {
                Assert.Equal("script", row!["id"]!.GetValue<string>());
                Assert.Equal("script", row["kind"]!.GetValue<string>());
                Assert.Equal("stable", row["channel"]!.GetValue<string>());
                Assert.Equal("active", row["status"]!.GetValue<string>());
            },
            row =>
            {
                Assert.Equal("script-2", row!["id"]!.GetValue<string>());
                Assert.Equal("failed: peer probe failed", row["status"]!.GetValue<string>());
                Assert.Equal("peer probe failed", row["statusReason"]!.GetValue<string>());
            },
            row =>
            {
                Assert.Equal("pr-17400", row!["id"]!.GetValue<string>());
                Assert.Equal("orphan-hive", row["kind"]!.GetValue<string>());
                Assert.Equal("no install found", row["status"]!.GetValue<string>());
            });
    }

    [Theory]
    [InlineData("dotnet-tool", "dotnet-tool", "Managed by dotnet tool; use: dotnet tool uninstall")]
    [InlineData("winget", "winget", "Managed by WinGet; use: winget uninstall")]
    [InlineData("brew", "homebrew", "Managed by Homebrew; use: brew uninstall")]
    public async Task InstallsList_ManagedInstalls_ShowPackageManagerCleanupHint(string source, string expectedManagedBy, string expectedHint)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, source, "aspire"),
                CanonicalPath = Path.Combine(aspireHome, source, "aspire"),
                Version = "13.2.0",
                Channel = "stable",
                Source = source,
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var json = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsArray();
        Assert.Equal(expectedManagedBy, json[0]!["managedBy"]!.GetValue<string>());
        Assert.Equal(expectedHint, json[0]!["cleanupHint"]!.GetValue<string>());
        Assert.Null(json[0]!["uninstallCommand"]);
    }

    [Theory]
    [InlineData("script", null, "script", "Use: aspire installs uninstall script")]
    [InlineData("pr", "pr-17416", "pr", "Use: aspire installs uninstall pr-17416")]
    [InlineData("localhive", "local", "localhive", "Use: aspire installs uninstall local")]
    [InlineData("future-source", "future", "future-source", "Use: aspire installs uninstall future")]
    public async Task InstallsList_AspireOwnedAndUnknownSources_UseAspireCleanupHint(string source, string? channel, string expectedKind, string expectedHint)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, source, "aspire"),
                CanonicalPath = Path.Combine(aspireHome, source, "aspire"),
                Version = "13.2.0",
                Channel = channel,
                Source = source,
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.Ok
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var row = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsArray()[0]!;
        Assert.Equal(expectedKind, row["kind"]!.GetValue<string>());
        Assert.Equal(expectedHint, row["cleanupHint"]!.GetValue<string>());
        Assert.Null(row["managedBy"]);
    }

    [Theory]
    [InlineData("dotnet-tool", "dotnet")]
    [InlineData("winget", "winget")]
    [InlineData("brew", "brew")]
    public async Task InstallsUninstall_ManagedInstalls_AreDenied(string source, string expectedCommand)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "stable", "packages"));

        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, source, "aspire"),
                CanonicalPath = Path.Combine(aspireHome, source, "aspire"),
                Version = "13.2.0",
                Channel = "stable",
                Source = source,
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs uninstall stable --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.True(Directory.Exists(Path.Combine(aspireHome, "hives", "stable")));
        Assert.Empty(testInteractionService.BooleanPromptCalls);
        Assert.Contains(testInteractionService.DisplayedErrors, e => e.Contains($"use: {expectedCommand}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InstallsUninstall_UnknownId_FailsWithoutPrompting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var testInteractionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs uninstall missing-id");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Empty(testInteractionService.BooleanPromptCalls);
        Assert.Contains(testInteractionService.DisplayedErrors, e => e.Contains("missing-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallsUninstall_ValidId_PromptsBeforeDeletingWhenYesIsNotSpecified()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        var hivePath = Path.Combine(aspireHome, "hives", "pr-17400");
        Directory.CreateDirectory(Path.Combine(hivePath, "packages"));
        var testInteractionService = new TestInteractionService();
        testInteractionService.SetupBooleanResponse(false);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => testInteractionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs uninstall pr-17400");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Cancelled, exitCode);
        Assert.Single(testInteractionService.BooleanPromptCalls);
        Assert.True(Directory.Exists(hivePath));
    }

    [Fact]
    public async Task InstallsList_OrdersActiveFirstAndOmitsMissingHivePaths()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-17400", "packages"));

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, "daily", "aspire"),
                CanonicalPath = Path.Combine(aspireHome, "daily", "aspire"),
                Version = "13.2.0",
                Channel = "daily",
                Source = "script",
                PathStatus = InstallationPathStatus.Shadowed,
                Status = InstallationInfoStatus.Ok
            },
            [
                new InstallationInfo
                {
                    Path = Path.Combine(aspireHome, "bin", "aspire"),
                    CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                    Version = "13.2.0",
                    Channel = "stable",
                    Source = "script",
                    PathStatus = InstallationPathStatus.Active,
                    Status = InstallationInfoStatus.Ok
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var json = JsonNode.Parse(string.Join(Environment.NewLine, outputWriter.Logs))!.AsArray();
        Assert.Equal("script-2", json[0]!["id"]!.GetValue<string>());
        Assert.Equal("active", json[0]!["status"]!.GetValue<string>());
        Assert.Equal("script", json[1]!["id"]!.GetValue<string>());
        Assert.Null(json[1]!["hive"]);
        Assert.Equal("pr-17400", json[2]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task InstallsList_HidesDotnetHostSelfRowAndUsesUniqueIds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");

        var outputWriter = new TestOutputTextWriter(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = outputWriter;
            options.DisableAnsi = true;
        });
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/opt/homebrew/Cellar/dotnet/10.0.107/libexec/dotnet",
                CanonicalPath = "/opt/homebrew/Cellar/dotnet/10.0.107/libexec/dotnet",
                Version = "13.2.0",
                Channel = "local",
                Source = null,
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.Ok
            },
            [
                new InstallationInfo
                {
                    Path = Path.Combine(aspireHome, "bin", "aspire"),
                    CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                    Version = "13.2.0",
                    Channel = "local",
                    Source = "localhive",
                    PathStatus = InstallationPathStatus.Active,
                    Status = InstallationInfoStatus.Ok
                },
                new InstallationInfo
                {
                    Path = Path.Combine(aspireHome, "dogfood", "pr-1", "bin", "aspire"),
                    CanonicalPath = Path.Combine(aspireHome, "dogfood", "pr-1", "bin", "aspire"),
                    Version = "13.2.0",
                    Channel = "local",
                    Source = "localhive",
                    PathStatus = InstallationPathStatus.Shadowed,
                    Status = InstallationInfoStatus.Ok
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        var output = string.Join(Environment.NewLine, outputWriter.Logs);
        Assert.DoesNotContain("/opt/homebrew/Cellar/dotnet", output);
        Assert.Contains("local", output);
        Assert.Contains("local-2", output);
    }

    [Fact]
    public async Task InstallsList_LogsClassificationReasons()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "pr-17400", "packages"));
        var logger = new CapturingLogger<InstallsCommand>();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.Replace(ServiceDescriptor.Singleton<ILogger<InstallsCommand>>(logger));
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = "/opt/homebrew/Cellar/dotnet/10.0.107/libexec/dotnet",
                CanonicalPath = "/opt/homebrew/Cellar/dotnet/10.0.107/libexec/dotnet",
                Version = "13.2.0",
                Channel = "local",
                Source = null,
                PathStatus = InstallationPathStatus.NotOnPath,
                Status = InstallationInfoStatus.Ok
            },
            [
                new InstallationInfo
                {
                    Path = Path.Combine(aspireHome, "bin", "aspire"),
                    CanonicalPath = Path.Combine(aspireHome, "bin", "aspire"),
                    Version = "13.2.0",
                    Channel = "local",
                    Source = "localhive",
                    PathStatus = InstallationPathStatus.Active,
                    Status = InstallationInfoStatus.Ok
                }
            ])));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs list");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Ignoring discovery row path", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Classified install path", StringComparison.Ordinal) && e.Message.Contains("localhive", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Classified hive", StringComparison.Ordinal) && e.Message.Contains("orphan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallsUninstall_OrphanHive_DeletesHive()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        var hivePath = Path.Combine(aspireHome, "hives", "pr-17400");
        Directory.CreateDirectory(Path.Combine(hivePath, "packages"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs uninstall pr-17400 --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(Directory.Exists(hivePath));
    }

    [Fact]
    public async Task InstallsUninstall_PrNumberId_DeletesExactHive()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        var hivePath = Path.Combine(aspireHome, "hives", "pr-17400");
        Directory.CreateDirectory(Path.Combine(hivePath, "packages"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs uninstall pr-17400 --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(Directory.Exists(hivePath));
    }

    [Fact]
    public async Task InstallsUninstall_SuffixedIdWithoutMatchingRow_RejectsRatherThanDeleteUnderlyingChannel()
    {
        // `aspire installs uninstall pr-17416-2` must NOT silently strip the
        // numeric suffix and delete the unsuffixed `pr-17416` hive. A typoed
        // id (one that BuildRowsAsync didn't actually materialize) is a
        // user error, not a license to delete a different hive.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire");
        var hivePath = Path.Combine(aspireHome, "hives", "pr-17416");
        Directory.CreateDirectory(Path.Combine(hivePath, "packages"));

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.Replace(ServiceDescriptor.Singleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            new InstallationInfo
            {
                Path = Path.Combine(aspireHome, "dogfood", "pr-17416", "bin", "aspire"),
                CanonicalPath = Path.Combine(aspireHome, "dogfood", "pr-17416", "bin", "aspire"),
                Version = "13.2.0",
                Channel = "pr-17416",
                Source = "pr",
                PathStatus = InstallationPathStatus.Active,
                Status = InstallationInfoStatus.Ok
            })));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("installs uninstall pr-17416-2 --yes");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        Assert.True(Directory.Exists(hivePath), "Underlying hive must not be deleted when the typoed id has no matching row.");
    }
}
