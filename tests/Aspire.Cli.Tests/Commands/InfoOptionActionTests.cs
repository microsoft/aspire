// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class InfoOptionActionTests(ITestOutputHelper outputHelper)
{
    private static InstallationInfo CreateSelf() => new()
    {
        Path = "/self/bin/aspire",
        CanonicalPath = "/self/bin/aspire",
        Version = "13.4.0",
        Channel = "stable",
        Route = "script",
        PathStatus = InstallationPathStatus.Active,
        Status = InstallationInfoStatus.Ok,
    };

    private static InfoOptionAction CreateAction(
        TemporaryWorkspace workspace,
        FakeInstallationDiscovery discovery,
        TestInteractionService interactionService,
        out CliExecutionContext executionContext,
        string identityChannel = "stable",
        string identityVersion = "13.4.0")
    {
        executionContext = workspace.CreateExecutionContext(identityChannel: identityChannel, identityVersion: identityVersion);
        var hiveEnumerator = new HiveEnumerator(executionContext, NullLogger<HiveEnumerator>.Instance);
        var wingetProbe = new WingetFirstRunProbe(new TestWindowsRegistryReader(), NullLogger<WingetFirstRunProbe>.Instance);

        return new InfoOptionAction(
            discovery,
            hiveEnumerator,
            wingetProbe,
            executionContext,
            interactionService,
            NullLogger<InfoOptionAction>.Instance);
    }

    // ---------------------------------------------------------------------------
    // Full JSON: exactly the {version, channel, installs} envelope, source not route
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_FullJson_WritesVersionChannelInstallsEnvelope()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var discovery = new FakeInstallationDiscovery(self);
        var interactionService = new TestInteractionService();
        var action = CreateAction(
            workspace,
            discovery,
            interactionService,
            out _,
            identityChannel: "staging",
            identityVersion: "13.5.0-emulated");

        var exitCode = await action.ExecuteAsync(selfOnly: false, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var json = Assert.Single(interactionService.DisplayedPlainText);

        var output = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InfoOutput);
        Assert.NotNull(output);
        Assert.Equal("13.5.0-emulated", output.Version);
        Assert.Equal("staging", output.Channel);
        var install = Assert.Single(output.Installs);
        Assert.Equal("13.4.0", install.Version);
        Assert.Equal("stable", install.Channel);
        Assert.Equal("script", install.Source);

        // The full envelope — an object, not a bare array — is the full-mode JSON contract.
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.True(document.RootElement.GetProperty("installs")[0].GetProperty("isCurrent").GetBoolean());

        // "source" is the wire field; the legacy "route" name must never appear.
        Assert.Contains("\"source\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"route\"", json, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // Self JSON: bare one-element array, no envelope
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_SelfJson_WritesBareOneElementArray()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var discovery = new FakeInstallationDiscovery(self);
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        var exitCode = await action.ExecuteAsync(selfOnly: true, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var json = Assert.Single(interactionService.DisplayedPlainText);

        // Self mode's contract is a bare array, not the {version, channel, installs} envelope.
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.DoesNotContain("\"installs\"", json, StringComparison.Ordinal);
        Assert.True(document.RootElement[0].GetProperty("isCurrent").GetBoolean());

        var installs = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InfoInstallationArray);
        Assert.NotNull(installs);
        var only = Assert.Single(installs);
        Assert.Equal("script", only.Source);
        Assert.Equal("/self/bin/aspire", only.Path);
    }

    [Fact]
    public async Task ExecuteAsync_SelfJson_DoesNotEnumerateOrReportHives()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var discovery = new FakeInstallationDiscovery(self);
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out var executionContext);
        Directory.CreateDirectory(Path.Combine(executionContext.HivesDirectory.FullName, "stable"));

        await action.ExecuteAsync(selfOnly: true, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        var json = Assert.Single(interactionService.DisplayedPlainText);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement[0].TryGetProperty("hive", out _));
    }

    // ---------------------------------------------------------------------------
    // Discovery seam: self mode must never invoke full discovery
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_SelfMode_DoesNotCallFullDiscovery()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var fullDiscoveryCalled = false;
        var discovery = new FakeInstallationDiscovery(self)
        {
            DiscoverAllAsyncCallback = _ =>
            {
                fullDiscoveryCalled = true;
                return Task.FromResult<IReadOnlyList<InstallationInfo>>([self]);
            },
        };
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        await action.ExecuteAsync(selfOnly: true, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        Assert.False(fullDiscoveryCalled);
    }

    [Fact]
    public async Task ExecuteAsync_FullMode_CallsFullDiscovery()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var fullDiscoveryCalled = false;
        var discovery = new FakeInstallationDiscovery(self)
        {
            DiscoverAllAsyncCallback = _ =>
            {
                fullDiscoveryCalled = true;
                return Task.FromResult<IReadOnlyList<InstallationInfo>>([self]);
            },
        };
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        await action.ExecuteAsync(selfOnly: false, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        Assert.True(fullDiscoveryCalled);
    }

    // ---------------------------------------------------------------------------
    // Diagnostic failure rows still return success
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_FullMode_DiscoveryThrows_StillReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var discovery = new FakeInstallationDiscovery(self)
        {
            DiscoverAllAsyncCallback = _ => throw new InvalidOperationException("discovery exploded"),
        };
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        var exitCode = await action.ExecuteAsync(selfOnly: false, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var json = Assert.Single(interactionService.DisplayedPlainText);
        var output = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InfoOutput);
        Assert.NotNull(output);
        var row = Assert.Single(output.Installs);
        Assert.Equal(InfoInstallationKind.DiscoveryFailed, row.Kind);
        Assert.Equal(InstallationInfoStatus.Failed, row.Status);
    }

    [Fact]
    public async Task ExecuteAsync_SelfMode_ProbeThrows_StillReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var discovery = new FakeInstallationDiscovery(self)
        {
            DescribeSelfCallback = () => throw new InvalidOperationException("self probe exploded"),
        };
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        var exitCode = await action.ExecuteAsync(selfOnly: true, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var json = Assert.Single(interactionService.DisplayedPlainText);
        var installs = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InfoInstallationArray);
        Assert.NotNull(installs);
        var row = Assert.Single(installs);
        Assert.Equal(InfoInstallationKind.DiscoveryFailed, row.Kind);
    }

    // ---------------------------------------------------------------------------
    // Non-ASCII regression: paths and channels must be literal, not \uXXXX
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_FullJson_NonAsciiPath_LiteralNotEscaped()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = new InstallationInfo
        {
            Path = "/home/日本語/aspire",
            CanonicalPath = "/home/日本語/aspire",
            Version = "13.4.0",
            Channel = "café",
            Route = "script",
            PathStatus = InstallationPathStatus.Active,
            Status = InstallationInfoStatus.Ok,
        };
        var discovery = new FakeInstallationDiscovery(self);
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        await action.ExecuteAsync(selfOnly: false, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        var json = Assert.Single(interactionService.DisplayedPlainText);

        // Must contain the literal multibyte characters, never \uXXXX sequences.
        Assert.Contains("日本語", json, StringComparison.Ordinal);
        Assert.Contains("café", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);

        // Must still parse to the expected shape.
        var output = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InfoOutput);
        Assert.NotNull(output);
        var install = Assert.Single(output.Installs);
        Assert.Equal("/home/日本語/aspire", install.Path);
        Assert.Equal("café", install.Channel);
    }

    [Fact]
    public async Task ExecuteAsync_SelfJson_NonAsciiPath_LiteralNotEscaped()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = new InstallationInfo
        {
            Path = "/home/日本語/aspire",
            CanonicalPath = "/home/日本語/aspire",
            Version = "13.4.0",
            Channel = "café",
            Route = "script",
            PathStatus = InstallationPathStatus.Active,
            Status = InstallationInfoStatus.Ok,
        };
        var discovery = new FakeInstallationDiscovery(self);
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        await action.ExecuteAsync(selfOnly: true, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        var json = Assert.Single(interactionService.DisplayedPlainText);

        // Must contain the literal multibyte characters, never \uXXXX sequences.
        Assert.Contains("日本語", json, StringComparison.Ordinal);
        Assert.Contains("café", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);

        // Must still parse to the expected shape.
        var installs = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InfoInstallationArray);
        Assert.NotNull(installs);
        var only = Assert.Single(installs);
        Assert.Equal("/home/日本語/aspire", only.Path);
        Assert.Equal("café", only.Channel);
    }

    // ---------------------------------------------------------------------------
    // Caller cancellation propagates
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CallerCancellation_Propagates()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var discovery = new FakeInstallationDiscovery(self);
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => action.ExecuteAsync(selfOnly: false, InfoOutputFormat.Json, cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_SelfMode_DescribeSelfCancellation_Propagates()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var discovery = new FakeInstallationDiscovery(self)
        {
            DescribeSelfCallback = () => throw new OperationCanceledException("cancelled"),
        };
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => action.ExecuteAsync(selfOnly: true, InfoOutputFormat.Json, TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------------------
    // Human mode delegates rendering
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_HumanMode_DisplaysExactlyOneRenderable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var discovery = new FakeInstallationDiscovery(self);
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        var exitCode = await action.ExecuteAsync(selfOnly: false, InfoOutputFormat.List, TestContext.Current.CancellationToken);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Single(interactionService.DisplayedRenderables);
        Assert.Empty(interactionService.DisplayedPlainText);
    }

    [Fact]
    public async Task ExecuteAsync_SelfMode_EmitsExactlyOneRow()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var self = CreateSelf();
        var others = new[]
        {
            new InstallationInfo
            {
                Path = "/peer/aspire",
                PathStatus = InstallationPathStatus.Shadowed,
                Status = InstallationInfoStatus.Ok,
            },
        };
        var discovery = new FakeInstallationDiscovery(self, others);
        var interactionService = new TestInteractionService();
        var action = CreateAction(workspace, discovery, interactionService, out _);

        await action.ExecuteAsync(selfOnly: true, InfoOutputFormat.Json, TestContext.Current.CancellationToken);

        var json = Assert.Single(interactionService.DisplayedPlainText);
        var installs = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.InfoInstallationArray);
        Assert.NotNull(installs);
        // Only the running CLI's own row — the peer must not leak into self output.
        var only = Assert.Single(installs);
        Assert.Equal("/self/bin/aspire", only.Path);
    }
}
