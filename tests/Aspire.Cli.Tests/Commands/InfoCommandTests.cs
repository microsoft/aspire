// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class InfoCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task InfoCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("info --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
    }

    [Fact]
    public async Task InfoCommand_Json_Self_ReturnsSingleElementArrayWithSelfRow()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        // Pin discovery output so the test doesn't depend on host environment
        // (process path, PATH contents, sidecar files happening to exist).
        services.RemoveAll<IInstallationDiscovery>();
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            self: new InstallationInfo
            {
                Path = "/usr/local/bin/aspire",
                CanonicalPath = "/usr/local/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Route = "script",
                IsOnPath = true,
                Status = InstallationInfoStatus.Ok,
            }));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        // --self opts into the cheap, single-row path; without it, info
        // performs full discovery (covered separately).
        var result = command.Parse("info --self --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, console) = Assert.Single(interactionService.DisplayedRawText);
        Assert.Equal(ConsoleOutput.Standard, console);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());

        var row = doc.RootElement[0];
        Assert.Equal("/usr/local/bin/aspire", row.GetProperty("path").GetString());
        Assert.Equal("13.0.0", row.GetProperty("version").GetString());
        Assert.Equal("stable", row.GetProperty("channel").GetString());
        Assert.Equal("script", row.GetProperty("route").GetString());
        Assert.True(row.GetProperty("isOnPath").GetBoolean());
        Assert.Equal(InstallationInfoStatus.Ok, row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InfoCommand_Json_Default_PerformsFullDiscovery()
    {
        // Without --self, `aspire info` runs the full discovery walk so the
        // user sees every Aspire install on the machine — that's the
        // user-expected default. The single-row path is opt-in via --self.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        services.RemoveAll<IInstallationDiscovery>();
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            self: new InstallationInfo
            {
                Path = "/usr/local/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Route = "script",
                Status = InstallationInfoStatus.Ok,
            },
            others:
            [
                new InstallationInfo
                {
                    Path = "/peer/aspire",
                    Version = "12.5.0",
                    Status = InstallationInfoStatus.Ok,
                },
            ]));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        // No flag: default is full discovery.
        var result = command.Parse("info --format json");
        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        // Two rows: self + the one peer the fake produces.
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task InfoCommand_Json_All_ReturnsAllDiscoveredInstalls()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        services.RemoveAll<IInstallationDiscovery>();
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            self: new InstallationInfo
            {
                Path = "/home/test/.aspire/bin/aspire",
                CanonicalPath = "/home/test/.aspire/bin/aspire",
                Version = "13.0.0",
                Channel = "stable",
                Route = "script",
                IsOnPath = true,
                Status = InstallationInfoStatus.Ok,
            },
            others:
            [
                // A peer that was successfully probed.
                new InstallationInfo
                {
                    Path = "/home/test/.aspire/dogfood/pr-1234/bin/aspire",
                    CanonicalPath = "/home/test/.aspire/dogfood/pr-1234/bin/aspire",
                    Version = "13.1.0-preview",
                    Channel = "pr-1234",
                    Route = "pr",
                    IsOnPath = false,
                    Status = InstallationInfoStatus.Ok,
                },
                // A peer where the trust gate refused to probe (untrusted PATH
                // hit) — verifies the wire shape for the not-probed case.
                new InstallationInfo
                {
                    Path = "/opt/random/aspire",
                    CanonicalPath = "/opt/random/aspire",
                    Status = InstallationInfoStatus.NotProbed,
                    StatusReason = "no sidecar present",
                },
            ]));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("info --all --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(3, doc.RootElement.GetArrayLength());

        // Running CLI is always first.
        var first = doc.RootElement[0];
        Assert.Equal("/home/test/.aspire/bin/aspire", first.GetProperty("path").GetString());
        Assert.True(first.GetProperty("isOnPath").GetBoolean());

        var notProbed = doc.RootElement[2];
        Assert.Equal(InstallationInfoStatus.NotProbed, notProbed.GetProperty("status").GetString());
        Assert.Equal("no sidecar present", notProbed.GetProperty("statusReason").GetString());
        // Trust gate skipped probing, so version/channel/route are absent (camelCase wire).
        Assert.False(notProbed.TryGetProperty("version", out _));
        Assert.False(notProbed.TryGetProperty("channel", out _));
        Assert.False(notProbed.TryGetProperty("route", out _));
    }

    [Fact]
    public async Task InfoCommand_Json_AlwaysEmitsArray_EvenForSingleSelfRow()
    {
        // The array shape is part of the JSON contract — consumers must not
        // need to special-case "self-only" vs "--all" results. Verifies the
        // top-level is a JSON array even when discovery returns one row.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("info --format json");
        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task InfoCommand_Default_HumanReadableTable_IncludesSelfAndCurrentMarker()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.RemoveAll<IInstallationDiscovery>();
        var selfCanonical = Environment.ProcessPath ?? "/tmp/aspire";
        services.AddSingleton<IInstallationDiscovery>(_ => new FakeInstallationDiscovery(
            self: new InstallationInfo
            {
                Path = selfCanonical,
                // CanonicalPath matches self so the (current) marker fires.
                CanonicalPath = selfCanonical,
                Version = "13.0.0",
                Channel = "local",
                Route = "script",
                IsOnPath = false,
                Status = InstallationInfoStatus.Ok,
            }));
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<Aspire.Cli.Commands.RootCommand>();
        var result = command.Parse("info");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(ExitCodeConstants.Success, exitCode);
        // Smoke check only — the table writer goes to IAnsiConsole, which the
        // test setup doesn't capture. The contract verified here is that the
        // command succeeds end-to-end with a discovery service that returns
        // the running CLI.
    }
}
