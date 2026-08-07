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
        var appHost = await DistributedApplicationTestFactory.CreateWithHostSettingsAsync(
            typeof(Projects.Godot_AppHost),
            testOutput,
            (_, settings) => settings.Configuration = BuildConfiguration(("GODOT_BIN", SentinelGodotBin)));
        await using var app = await appHost.BuildAsync();

        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var godotServer = Assert.Single(applicationModel.Resources.OfType<ExecutableResource>(), r => r.Name == "godot-server");

        Assert.Equal(SentinelGodotBin, godotServer.Command);
    }

    [Fact]
    public async Task WhitespaceGodotBinFallsBackToDefaultExecutable()
    {
        var appHost = await DistributedApplicationTestFactory.CreateWithHostSettingsAsync(
            typeof(Projects.Godot_AppHost),
            testOutput,
            (_, settings) => settings.Configuration = BuildConfiguration(("GODOT_BIN", "   ")));
        await using var app = await appHost.BuildAsync();

        var applicationModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var godotServer = Assert.Single(applicationModel.Resources.OfType<ExecutableResource>(), r => r.Name == "godot-server");

        Assert.Equal(OperatingSystem.IsWindows() ? "godot.exe" : "godot", godotServer.Command);
    }

    [Fact]
    public async Task GodotServerIsNotPartOfThePublishModel()
    {
        var appHost = await DistributedApplicationTestFactory.CreateWithHostSettingsAsync(
            typeof(Projects.Godot_AppHost),
            testOutput,
            (_, settings) => settings.Configuration = BuildConfiguration(
                ("AppHost:Operation", "publish"),
                ("Publishing:Publisher", "manifest")));
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
        Assert.DoesNotContain(executionConfiguration.EnvironmentVariables, kvp => kvp.Key.Contains("godot-server", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Seeds configuration the AppHost reads while it constructs resources. This has to be an in-memory
    /// configuration source applied through <c>HostApplicationBuilderSettings</c> so it is visible
    /// before Program.cs runs, and so it never mutates ambient process environment variables that other
    /// concurrently executing tests would observe.
    /// </summary>
    private static ConfigurationManager BuildConfiguration(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));

        return configuration;
    }
}
