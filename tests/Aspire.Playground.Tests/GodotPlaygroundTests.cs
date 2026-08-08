// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SamplesIntegrationTests;
using SamplesIntegrationTests.Infrastructure;
using Xunit;

namespace Aspire.Playground.Tests;

public class GodotPlaygroundTests(ITestOutputHelper testOutput)
{
    private const string SentinelGodotBin = "custom-godot-sentinel";
    private const string WhitespaceGodotBin = "   ";

    [Fact]
    public async Task AppHostStartsWithoutGodotAndKeepsGodotServerExplicitlyStopped()
    {
        var appHost = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.Godot_AppHost), testOutput);
        await using var app = await appHost.BuildAsync();

        await app.StartAsync();

        await app.WaitForResource("godot-server", KnownResourceStates.NotStarted).WaitAsync(TimeSpan.FromMinutes(5));

        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.DoesNotContain(applicationModel.Resources, r => r.Name == "matchmaker");

        var godotServer = Assert.Single(applicationModel.Resources.OfType<ExecutableResource>(), r => r.Name == "godot-server");
        var gameEndpoint = Assert.Single(godotServer.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "game");

        Assert.Equal(ProtocolType.Udp, gameEndpoint.Protocol);
        Assert.False(gameEndpoint.IsProxied);
        Assert.Equal("udp", gameEndpoint.UriScheme);
        var allocatedEndpoint = gameEndpoint.AllocatedEndpoint;
        Assert.NotNull(allocatedEndpoint);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var executionConfiguration = await ExecutionConfigurationBuilder.Create(godotServer)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        Assert.Null(executionConfiguration.Exception);

        var environmentVariables = executionConfiguration.EnvironmentVariables.ToDictionary();
        Assert.True(
            environmentVariables.TryGetValue("GODOT_SERVER_PORT", out var configuredPort),
            "Expected the game endpoint to flow its allocated port through GODOT_SERVER_PORT.");
        Assert.Equal(allocatedEndpoint.Port.ToString(CultureInfo.InvariantCulture), configuredPort);

        app.EnsureNoErrorsLogged();
        await app.StopAsync();
    }

    [Fact]
    public async Task GodotBinConfigurationOverridesDefaultExecutable()
    {
        var appHost = await DistributedApplicationTestFactory.CreateWithArgsAsync(
            typeof(Projects.Godot_AppHost),
            testOutput,
            [$"--GODOT_BIN={SentinelGodotBin}"]);
        await using var app = await appHost.BuildAsync();

        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var godotServer = Assert.Single(applicationModel.Resources.OfType<ExecutableResource>(), r => r.Name == "godot-server");

        // The command-line provider is added after the environment variable provider, so this value wins
        // even when the test process was launched with an ambient GODOT_BIN.
        Assert.Equal(SentinelGodotBin, godotServer.Command);

        // Passing args must not cost us the testing builder's defaults. DistributedApplicationFactory seeds
        // these into HostApplicationBuilderSettings.Configuration; replacing that manager would silently drop
        // random ports, resource cleanup waits and the dashboard/OTLP defaults.
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("true", configuration["DcpPublisher:RandomizePorts"]);
        Assert.Equal("true", configuration["DcpPublisher:WaitForResourceCleanup"]);

        // Only assert the key survived. Pinning the literal duration would couple this playground test to a
        // testing-builder default that is free to change without affecting anything this test cares about.
        Assert.NotNull(configuration["DcpPublisher:ContainerRuntimeInitializationTimeout"]);
    }

    [Fact]
    public async Task WhitespaceGodotBinFallsBackToDefaultExecutable()
    {
        var appHost = await DistributedApplicationTestFactory.CreateWithArgsAsync(
            typeof(Projects.Godot_AppHost),
            testOutput,
            [$"--GODOT_BIN={WhitespaceGodotBin}"]);
        await using var app = await appHost.BuildAsync();

        // Prove the whitespace actually reached the AppHost's configuration first. Without this, a regression
        // in arg plumbing would leave GODOT_BIN unset and the fallback assertion below would still pass while
        // testing nothing.
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(WhitespaceGodotBin, configuration["GODOT_BIN"]);

        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var godotServer = Assert.Single(applicationModel.Resources.OfType<ExecutableResource>(), r => r.Name == "godot-server");

        Assert.Equal(OperatingSystem.IsWindows() ? "godot.exe" : "godot", godotServer.Command);
    }

    [Fact]
    public async Task GodotServerIsNotPartOfThePublishModel()
    {
        var appHost = await DistributedApplicationTestFactory.CreateWithArgsAsync(
            typeof(Projects.Godot_AppHost),
            testOutput,
            ["--AppHost:Operation=publish", "--Publishing:Publisher=manifest"]);
        await using var app = await appHost.BuildAsync();

        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The AppHost really is in publish mode, otherwise this test would assert nothing.
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        Assert.True(executionContext.IsPublishMode);

        // godot-server is explicit-start, which only means anything in run mode, so it must not leak
        // into the publish model at all.
        Assert.DoesNotContain(applicationModel.Resources, r => r.Name == "godot-server");
    }
}
