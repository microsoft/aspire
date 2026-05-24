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
}
