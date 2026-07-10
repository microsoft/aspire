// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class DeckApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task GetConfig_ReturnsDeckConfigContract()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(isEnabled: true, applicationName: "StressApp")));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        var response = await httpClient.GetAsync("/api/deck/config").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync().DefaultTimeout());
        var expected = new JsonObject
        {
            ["applicationName"] = "StressApp",
            ["resourceServiceUrl"] = null,
            ["otlpGrpcUrl"] = null,
            ["otlpHttpUrl"] = null,
            ["version"] = VersionHelpers.DashboardDisplayVersion ?? string.Empty
        };

        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
    }

    [Fact]
    public async Task GetResources_ReturnsDeckResourceContract()
    {
        var resource = CreateResource();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(isEnabled: true, initialResources: [resource])));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        var response = await httpClient.GetAsync("/api/deck/resources").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync().DefaultTimeout());
        var expected = JsonNode.Parse(
            """
            [
              {
                "name": "frontend-abc123",
                "resourceType": "Project",
                "displayName": "frontend",
                "uid": "resource-uid",
                "state": "Running",
                "stateStyle": "success",
                "health": "Healthy",
                "createdAt": "2026-07-10T08:00:00Z",
                "startedAt": "2026-07-10T08:00:01Z",
                "stoppedAt": null,
                "urls": [
                  {
                    "name": "https",
                    "url": "https://localhost:7443/",
                    "isInternal": false,
                    "isInactive": false,
                    "displayName": "HTTPS",
                    "sortOrder": 10
                  }
                ],
                "properties": [
                  {
                    "name": "connectionString",
                    "displayName": "Connection string",
                    "value": "Server=database",
                    "isSensitive": true,
                    "isHighlighted": true,
                    "sortOrder": 20
                  }
                ],
                "environment": [
                  {
                    "name": "API_KEY",
                    "value": "secret",
                    "isFromSpec": true
                  }
                ],
                "healthReports": [
                  {
                    "status": "Healthy",
                    "key": "ready",
                    "description": "Ready to serve traffic."
                  }
                ],
                "commands": [
                  {
                    "name": "restart",
                    "displayName": "Restart",
                    "displayDescription": "Restart this resource.",
                    "confirmationMessage": "Restart frontend?",
                    "iconName": "ArrowClockwise",
                    "isHighlighted": true,
                    "state": "enabled"
                  }
                ],
                "relationships": [
                  {
                    "resourceName": "database",
                    "type": "Reference"
                  }
                ],
                "isHidden": true,
                "supportsDetailedTelemetry": true,
                "iconName": "Code"
              }
            ]
            """);

        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
    }

    [Fact]
    public async Task GetResources_BrowserTokenAuthWithoutCookie_RedirectsToLogin()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "TestKey123!";
        });
        await app.StartAsync().DefaultTimeout();

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}")
        };
        var response = await httpClient.GetAsync("/api/deck/resources").DefaultTimeout();

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?returnUrl=%2Fapi%2Fdeck%2Fresources", response.Headers.Location?.PathAndQuery);
    }

    private static ResourceViewModel CreateResource()
    {
        var property = new ResourcePropertyViewModel(
            name: "connectionString",
            value: Value.ForString("Server=database"),
            isValueSensitive: true,
            knownProperty: null,
            sortOrder: 20,
            displayName: "Connection string",
            isHighlighted: true);

        return new ResourceViewModel
        {
            Name = "frontend-abc123",
            ResourceType = KnownResourceTypes.Project,
            DisplayName = "frontend",
            Uid = "resource-uid",
            ReplicaIndex = 0,
            State = KnownResourceState.Running.ToString(),
            StateStyle = "success",
            CreationTimeStamp = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc),
            StartTimeStamp = new DateTime(2026, 7, 10, 8, 0, 1, DateTimeKind.Utc),
            StopTimeStamp = null,
            Environment = [new EnvironmentVariableViewModel("API_KEY", "secret", fromSpec: true)],
            Urls =
            [
                new UrlViewModel(
                    "https",
                    new Uri("https://localhost:7443"),
                    isInternal: false,
                    isInactive: false,
                    new UrlDisplayPropertiesViewModel("HTTPS", 10))
            ],
            Volumes = [],
            Relationships = [new RelationshipViewModel("database", "Reference")],
            Properties = ImmutableDictionary<string, ResourcePropertyViewModel>.Empty.Add(property.Name, property),
            Commands =
            [
                new CommandViewModel(
                    "restart",
                    CommandViewModelState.Enabled,
                    "Restart",
                    "Restart this resource.",
                    "Restart frontend?",
                    argumentInputs: [],
                    isHighlighted: true,
                    "ArrowClockwise",
                    IconVariant.Regular)
            ],
            HealthReports = [new HealthReportViewModel("ready", HealthStatus.Healthy, "Ready to serve traffic.", ExceptionText: null)],
            KnownState = KnownResourceState.Running,
            IsHidden = true,
            SupportsDetailedTelemetry = true,
            IconName = "Code",
            IconVariant = IconVariant.Regular
        };
    }
}
