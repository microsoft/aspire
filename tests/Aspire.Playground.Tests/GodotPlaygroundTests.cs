// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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
    public async Task AppHostStartsWithoutGodotAndExposesMatchmakerEndpointConfiguration()
    {
        var appHost = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.Godot_AppHost), testOutput);
        await using var app = await appHost.BuildAsync();

        await app.StartAsync();

        await Task.WhenAll(
            app.WaitForResource("matchmaker", KnownResourceStates.Running),
            app.WaitForResource("godot-server", KnownResourceStates.NotStarted)).WaitAsync(TimeSpan.FromMinutes(5));

        using var client = AppHostTests.CreateHttpClientWithResilience(app, "matchmaker");
        using var response = await client.GetAsync("/configuration");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = payload.RootElement;

        Assert.Equal("godot-server", root.GetProperty("resourceName").GetString());
        Assert.True(root.GetProperty("endpointConfigured").GetBoolean());

        var port = root.GetProperty("configuredPort").GetInt32();
        var endpoint = root.GetProperty("configuredEndpoint").GetString();

        Assert.InRange(port, 10000, 32767);
        Assert.True(Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri), $"Expected a valid endpoint URI, got '{endpoint}'.");
        Assert.NotNull(endpointUri);
        Assert.Equal("udp", endpointUri.Scheme);
        Assert.Equal("localhost", endpointUri.Host);
        Assert.Equal(port, endpointUri.Port);

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
        // into the publish model at all — neither as a resource nor as a matchmaker reference.
        Assert.DoesNotContain(applicationModel.Resources, r => r.Name == "godot-server");

        // Resolve the execution context from the app rather than constructing one: only the
        // DI-registered instance carries the AppHost's IServiceProvider, which
        // ExecutionConfigurationBuilder needs to resolve value providers.
        var matchmaker = Assert.Single(applicationModel.Resources.OfType<IResourceWithEnvironment>(), r => r.Name == "matchmaker");
        var executionConfiguration = await ExecutionConfigurationBuilder.Create(matchmaker)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext);

        // A resolution failure is reported here rather than thrown, and it yields an empty collection, which
        // would make the negative assertion below pass without proving anything.
        Assert.Null(executionConfiguration.Exception);

        Assert.DoesNotContain(
            executionConfiguration.EnvironmentVariables,
            kvp => kvp.Key.Contains("godot-server", StringComparison.OrdinalIgnoreCase)
                || kvp.Value.Contains("godot-server", StringComparison.OrdinalIgnoreCase));
    }
}
