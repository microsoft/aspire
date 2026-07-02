// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using SamplesIntegrationTests;
using SamplesIntegrationTests.Infrastructure;
using Xunit;

namespace Aspire.Playground.Tests;

public class GodotPlaygroundTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task AppHostStartsWithoutGodotAndExposesMatchmakerServerInfo()
    {
        var appHost = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.Godot_AppHost), testOutput);
        await using var app = await appHost.BuildAsync();

        await app.StartAsync();

        await Task.WhenAll(
            app.WaitForResource("matchmaker", KnownResourceStates.Running),
            app.WaitForResource("godot-server", KnownResourceStates.NotStarted)).WaitAsync(TimeSpan.FromMinutes(5));

        using var client = AppHostTests.CreateHttpClientWithResilience(app, "matchmaker");
        using var response = await client.GetAsync("/servers");
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = payload.RootElement;

        Assert.Equal("godot-server", root.GetProperty("resourceName").GetString());

        var port = root.GetProperty("port").GetInt32();
        var endpoint = root.GetProperty("endpoint").GetString();

        Assert.InRange(port, 1, 65535);
        Assert.NotEqual(7000, port);
        Assert.True(Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri), $"Expected a valid endpoint URI, got '{endpoint}'.");
        Assert.Equal("udp", endpointUri.Scheme);
        Assert.Equal("localhost", endpointUri.Host);
        Assert.Equal(port, endpointUri.Port);

        app.EnsureNoErrorsLogged();
        await app.StopAsync();
    }
}
