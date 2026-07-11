// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
using DashboardProto = global::Aspire.DashboardService.Proto.V1;

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
                    "iconVariant": "regular",
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
                "iconName": "Code",
                "iconVariant": "filled"
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

    [Fact]
    public async Task PostExecuteCommand_ExecutesResolvedCommandInteractively()
    {
        var resource = CreateResource();
        string? actualResourceName = null;
        string? actualResourceType = null;
        CommandViewModel? actualCommand = null;
        ExecuteResourceCommandOptions? actualOptions = null;
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(
                    isEnabled: true,
                    initialResources: [resource],
                    executeResourceCommand: (resourceName, resourceType, command, options, _) =>
                    {
                        actualResourceName = resourceName;
                        actualResourceType = resourceType;
                        actualCommand = command;
                        actualOptions = options;
                        return Task.FromResult(new ResourceCommandResponseViewModel
                        {
                            Kind = ResourceCommandResponseKind.Succeeded,
                            Message = "Restarted."
                        });
                    })));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var content = new StringContent(
            """
            {"resourceName":"frontend-abc123","commandName":"restart"}
            """,
            Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/api/deck/commands/execute", content).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(resource.Name, actualResourceName);
        Assert.Equal(resource.ResourceType, actualResourceType);
        Assert.Equal("restart", actualCommand?.Name);
        Assert.False(actualOptions?.NonInteractive);
        Assert.Null(actualOptions?.Arguments);
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync().DefaultTimeout());
        var expected = JsonNode.Parse(
            """
            {"kind":"succeeded","message":"Restarted."}
            """);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
    }

    [Theory]
    [InlineData("missing-resource", "restart")]
    [InlineData("frontend-abc123", "missing-command")]
    public async Task PostExecuteCommand_UnknownResourceOrCommand_ReturnsNotFound(string resourceName, string commandName)
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(isEnabled: true, initialResources: [CreateResource()])));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var content = new StringContent(
            $$"""
            {"resourceName":"{{resourceName}}","commandName":"{{commandName}}"}
            """,
            Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/api/deck/commands/execute", content).DefaultTimeout();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetConsoleLogs_StreamsBacklogAndLiveBatches()
    {
        var consoleLogs = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        string? requestedResourceName = null;
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(
                    isEnabled: true,
                    initialResources: [CreateResource()],
                    consoleLogsChannelProvider: resourceName =>
                    {
                        requestedResourceName = resourceName;
                        return consoleLogs;
                    })));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/deck/resources/frontend-abc123/console-logs");
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationTokenSource.Token).DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        Assert.Equal("frontend-abc123", requestedResourceName);

        await consoleLogs.Writer.WriteAsync(
            [
                new ResourceLogLine(12, "Listening on https://localhost:7443", IsErrorMessage: false),
                new ResourceLogLine(13, "Connection failed", IsErrorMessage: true)
            ],
            cancellationTokenSource.Token);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationTokenSource.Token);
        using var reader = new StreamReader(stream);
        var responseLine = await reader.ReadLineAsync(cancellationTokenSource.Token)
            ?? throw new InvalidOperationException("The console stream ended before emitting a batch.");
        var actual = JsonNode.Parse(responseLine);
        var expected = JsonNode.Parse(
            """
            {
              "resourceName": "frontend-abc123",
              "lines": [
                {
                  "lineNumber": 12,
                  "text": "Listening on https://localhost:7443",
                  "isStdErr": false
                },
                {
                  "lineNumber": 13,
                  "text": "Connection failed",
                  "isStdErr": true
                }
              ]
            }
            """);

        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
        consoleLogs.Writer.Complete();
    }

    [Fact]
    public async Task GetConsoleLogs_UnknownResource_ReturnsNotFound()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(isEnabled: true, initialResources: [CreateResource()])));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        var response = await httpClient.GetAsync("/api/deck/resources/missing/console-logs").DefaultTimeout();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Interactions_RoundTripInputsDialog()
    {
        var interactions = Channel.CreateUnbounded<DashboardProto.WatchInteractionsResponseUpdate>();
        var responses = Channel.CreateUnbounded<DashboardProto.WatchInteractionsRequestUpdate>();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            preConfigureBuilder: builder => builder.Services.AddSingleton<IDashboardClient>(
                new TestDashboardClient(
                    isEnabled: true,
                    interactionChannelProvider: () => interactions,
                    sendInteractionUpdateChannel: responses)));
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        var interaction = new DashboardProto.WatchInteractionsResponseUpdate
        {
            InteractionId = 42,
            Title = "Echo arguments",
            Message = "Provide command values.",
            PrimaryButtonText = "Run",
            SecondaryButtonText = "Cancel",
            ShowSecondaryButton = true,
            ShowDismiss = true,
            InputsDialog = new DashboardProto.InteractionInputsDialog()
        };
        var flavorInput = new DashboardProto.InteractionInput
        {
            Name = "flavor",
            Label = "Flavor",
            InputType = DashboardProto.InputType.Choice,
            Value = "vanilla",
            Description = "Select a flavor.",
            UpdateStateOnChange = true
        };
        flavorInput.Options.Add("vanilla", "Vanilla");
        flavorInput.Options.Add("chocolate", "Chocolate");
        interaction.InputsDialog.InputItems.Add(
            new DashboardProto.InteractionInput
            {
                Name = "message",
                Label = "Message",
                Placeholder = "Hello",
                InputType = DashboardProto.InputType.Text,
                Required = true,
                MaxLength = 80
            });
        interaction.InputsDialog.InputItems.Add(flavorInput);
        await interactions.Writer.WriteAsync(interaction).DefaultTimeout();

        var actual = await GetInteractionsAsync(httpClient, expectedCount: 1);
        var expected = JsonNode.Parse(
            """
            [
              {
                "interactionId": 42,
                "kind": "inputsDialog",
                "title": "Echo arguments",
                "message": "Provide command values.",
                "primaryButtonText": "Run",
                "secondaryButtonText": "Cancel",
                "showSecondaryButton": true,
                "showDismiss": true,
                "enableMessageMarkdown": false,
                "intent": "none",
                "inputs": [
                  {
                    "name": "message",
                    "label": "Message",
                    "placeholder": "Hello",
                    "inputType": "text",
                    "required": true,
                    "options": [],
                    "value": "",
                    "validationErrors": [],
                    "description": "",
                    "enableDescriptionMarkdown": false,
                    "maxLength": 80,
                    "allowCustomChoice": false,
                    "disabled": false,
                    "updateStateOnChange": false
                  },
                  {
                    "name": "flavor",
                    "label": "Flavor",
                    "placeholder": "",
                    "inputType": "choice",
                    "required": false,
                    "options": [
                      ["chocolate", "Chocolate"],
                      ["vanilla", "Vanilla"]
                    ],
                    "value": "vanilla",
                    "validationErrors": [],
                    "description": "Select a flavor.",
                    "enableDescriptionMarkdown": false,
                    "maxLength": 0,
                    "allowCustomChoice": false,
                    "disabled": false,
                    "updateStateOnChange": true
                  }
                ],
                "linkText": "",
                "linkUrl": ""
              }
            ]
            """);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");

        using var content = new StringContent(
            """
            {
              "interactionId": 42,
              "action": "submit",
              "values": {
                "message": "Hello from React",
                "flavor": "chocolate"
              }
            }
            """,
            Encoding.UTF8,
            "application/json");
        var response = await httpClient.PostAsync("/api/deck/interactions/respond", content).DefaultTimeout();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var sent = await responses.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(42, sent.InteractionId);
        Assert.Equal(DashboardProto.WatchInteractionsRequestUpdate.KindOneofCase.InputsDialog, sent.KindCase);
        Assert.True(sent.HasResponseUpdate);
        Assert.False(sent.ResponseUpdate);
        Assert.Collection(
            sent.InputsDialog.InputItems,
            input =>
            {
                Assert.Equal("message", input.Name);
                Assert.Equal("Hello from React", input.Value);
            },
            input =>
            {
                Assert.Equal("flavor", input.Name);
                Assert.Equal("chocolate", input.Value);
            });
        Assert.Empty(await GetInteractionsAsync(httpClient, expectedCount: 0));
    }

    private static async Task<JsonArray> GetInteractionsAsync(HttpClient httpClient, int expectedCount)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var response = await httpClient.GetAsync("/api/deck/interactions", cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token))!.AsArray();
            if (actual.Count == expectedCount)
            {
                return actual;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cts.Token);
        }
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
            IconVariant = IconVariant.Filled
        };
    }
}
