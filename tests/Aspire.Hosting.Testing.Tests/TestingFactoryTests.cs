// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using Aspire.TestUtilities;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Hosting.Testing.Tests;

public class TestingFactoryTests(DistributedApplicationFixture<Projects.TestingAppHost1_AppHost> fixture) : IClassFixture<DistributedApplicationFixture<Projects.TestingAppHost1_AppHost>>
{
    private readonly DistributedApplication _app = fixture.Application;

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void HasEndPoints()
    {
        // Get an endpoint from a resource
        var workerEndpoint = _app.GetEndpoint("myworker1", "myendpoint1");
        Assert.NotNull(workerEndpoint);
        Assert.True(workerEndpoint.Host.Length > 0);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task CanGetConnectionStringFromAddConnectionString()
    {
        // Get a connection string from a resource
        var connectionString = await _app.GetConnectionStringAsync("cs");
        var connectionString2 = await _app.GetConnectionStringAsync("cs2");
        Assert.Equal("testconnection", connectionString);
        Assert.Equal("Value=this is a value", connectionString2);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void CanGetResources()
    {
        var appModel = _app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Contains(appModel.GetProjectResources(), p => p.Name == "myworker1");
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task HttpClientGetTest()
    {
        // Wait for the application to be ready
        await _app.WaitForTextAsync("Application started.", "mywebapp1").WaitAsync(TimeSpan.FromMinutes(1));

        var httpClient = _app.CreateHttpClientWithResilience("mywebapp1");

        var result1 = await httpClient.GetFromJsonAsync<WeatherForecast[]>("/weatherforecast");
        Assert.NotNull(result1);
        Assert.True(result1.Length > 0);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void SetsCorrectContentRoot()
    {
        var appModel = _app.Services.GetRequiredService<IHostEnvironment>();
        Assert.Contains("TestingAppHost1", appModel.ContentRootPath);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/4650")]
    public async Task SelectsFirstLaunchProfile()
    {
        var config = _app.Services.GetRequiredService<IConfiguration>();
        var profileName = config["AppHost:DefaultLaunchProfileName"];
        Assert.Equal("https", profileName);

        // Wait for resource to start.
        await _app.ResourceNotifications.WaitForResourceAsync("mywebapp1").WaitAsync(TimeSpan.FromSeconds(60));

        // Explicitly get the HTTPS endpoint - this is only available on the "https" launch profile.
        var httpClient = _app.CreateHttpClientWithResilience("mywebapp1", "https");
        var result = await httpClient.GetFromJsonAsync<WeatherForecast[]>("/weatherforecast");
        Assert.NotNull(result);
        Assert.True(result.Length > 0);
    }

    private sealed record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
    {
        public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
    }
}
