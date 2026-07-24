// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.DashboardService.Proto.V1;
using Aspire.Shared;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ProtoResource = Aspire.DashboardService.Proto.V1.Resource;

namespace Aspire.Dashboard.Backend.Tests;

public class DashboardBackendApplicationTests
{
    [Fact]
    public async Task Discovery_AdvertisesImplementedVersionedCapabilities()
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(
            "{\"product\":\"Aspire.Dashboard\",\"versions\":[{\"version\":1,\"basePath\":\"/api/dashboard/v1\",\"capabilities\":[\"configuration\",\"resources\",\"resources-live\",\"commands\",\"structured-logs\",\"structured-logs-live\",\"traces\",\"traces-live\",\"traces-clear\",\"metrics\",\"metrics-series\",\"metrics-clear\",\"console-logs\",\"console-logs-live\",\"interactions\"]}]}",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("null")]
    [InlineData("file://")]
    public async Task DevelopmentAccessPolicy_RejectsNonLoopbackBrowserOrigins(string origin)
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, DashboardApiContract.DiscoveryPath);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:1430")]
    [InlineData("https://Stress.dev.localhost:49985")]
    [InlineData("http://127.0.0.1:1430")]
    [InlineData("http://[::1]:1430")]
    public async Task DevelopmentAccessPolicy_AllowsLoopbackBrowserOrigins(string origin)
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, DashboardApiContract.DiscoveryPath);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.42.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("2001:db8::1", false)]
    public void DevelopmentAccessPolicy_RestrictsConnectionsToLoopback(string address, bool expected)
    {
        Assert.Equal(expected, DashboardDevelopmentAccessPolicy.IsLoopback(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task GetConfiguration_ReturnsConfiguredIdentityFromVersionOneRoute()
    {
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DashboardBackend:ApplicationName"] = "Stress AppHost",
                ["DashboardBackend:Version"] = "13.5.0-aot"
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/v1/config", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("Stress AppHost", root.GetProperty("applicationName").GetString());
        Assert.Equal("13.5.0-aot", root.GetProperty("dashboardVersion").GetString());
        Assert.StartsWith(".NET", root.GetProperty("runtimeVersion").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task GetConfiguration_UsesProductVersionByDefault()
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync(
            "/api/dashboard/v1/config",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            AssemblyVersionHelper.GetDisplayVersion(typeof(DashboardBackendApplication).Assembly),
            document.RootElement.GetProperty("dashboardVersion").GetString());
    }

    [Fact]
    public async Task FrontendRoot_ServesHostedReactIndexWithoutCaching()
    {
        var assets = new TestFrontendAssetProvider(new Dictionary<string, string>
        {
            ["index.html"] = "<!doctype html><meta name=\"aspire-dashboard-backend\" content=\"standalone\" /><script src=\"./assets/index-AbCd1234.js\"></script><div id=\"root\"></div>"
        });
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardFrontendAssetProvider>(assets);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync("/", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "<meta name=\"aspire-dashboard-backend\" content=\"aot\" />",
            html,
            StringComparison.Ordinal);
        Assert.Contains("src=\"/assets/index-AbCd1234.js\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FrontendAssets_UseContentTypesAndImmutableCachingForHashedFiles()
    {
        var assets = new TestFrontendAssetProvider(new Dictionary<string, string>
        {
            ["assets/index-AbCd1234.js"] = "export const dashboard = true;"
        });
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardFrontendAssetProvider>(assets);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync(
            "/assets/index-AbCd1234.js",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            "export const dashboard = true;",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/consolelogs/resource/api", HttpStatusCode.OK)]
    [InlineData("/missing.js", HttpStatusCode.NotFound)]
    [InlineData("/api/not-a-dashboard-route", HttpStatusCode.NotFound)]
    public async Task FrontendFallback_OnlyHandlesSpaRoutes(string path, HttpStatusCode expectedStatus)
    {
        var assets = new TestFrontendAssetProvider(new Dictionary<string, string>
        {
            ["index.html"] = "<meta name=\"aspire-dashboard-backend\" content=\"standalone\" />"
        });
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardFrontendAssetProvider>(assets);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task VersionedAndLegacyAliases_UseDirectInteractionAndCommandServices()
    {
        var interaction = new DashboardInteraction(
            42,
            "inputsDialog",
            "Set parameter",
            "Provide a value.",
            "Apply",
            "Cancel",
            true,
            true,
            false,
            "none",
            [
                new DashboardInteractionInput(
                    "value",
                    "Value",
                    "Enter a value",
                    "text",
                    true,
                    [],
                    "initial",
                    [],
                    "",
                    false,
                    100,
                    false,
                    false,
                    true)
            ],
            "",
            "");
        var interactionService = new TestInteractionService([interaction]);
        var commandExecutor = new TestCommandExecutor(new DashboardCommandResponse("succeeded", null, null));
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardInteractionService>(interactionService);
            builder.Services.AddSingleton<IDashboardCommandExecutor>(commandExecutor);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var getResponse = await client.GetAsync(
            "/api/dashboard/v1/interactions",
            TestContext.Current.CancellationToken);

        getResponse.EnsureSuccessStatusCode();
        Assert.Equal("no-store", getResponse.Headers.CacheControl?.ToString());
        Assert.Equal(
            "[{\"interactionId\":42,\"kind\":\"inputsDialog\",\"title\":\"Set parameter\",\"message\":\"Provide a value.\",\"primaryButtonText\":\"Apply\",\"secondaryButtonText\":\"Cancel\",\"showSecondaryButton\":true,\"showDismiss\":true,\"enableMessageMarkdown\":false,\"intent\":\"none\",\"inputs\":[{\"name\":\"value\",\"label\":\"Value\",\"placeholder\":\"Enter a value\",\"inputType\":\"text\",\"required\":true,\"options\":[],\"value\":\"initial\",\"validationErrors\":[],\"description\":\"\",\"enableDescriptionMarkdown\":false,\"maxLength\":100,\"allowCustomChoice\":false,\"disabled\":false,\"updateStateOnChange\":true}],\"linkText\":\"\",\"linkUrl\":\"\"}]",
            await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var postResponse = await client.PostAsJsonAsync(
            "/api/dashboard/v1/interactions/respond",
            new { interactionId = 42, action = "submit", values = new { value = "updated" } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, postResponse.StatusCode);
        Assert.Equal(42, interactionService.Request?.InteractionId);
        Assert.Equal("submit", interactionService.Request?.Action);
        Assert.Equal("updated", interactionService.Request?.Values?["value"]);

        using var legacyGetResponse = await client.GetAsync(
            "/api/deck/interactions",
            TestContext.Current.CancellationToken);
        legacyGetResponse.EnsureSuccessStatusCode();
        Assert.Contains("\"interactionId\":42", await legacyGetResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        using var commandResponse = await client.PostAsJsonAsync(
            "/api/deck/commands/execute",
            new { resourceName = "db-connection-string", commandName = "set-parameter" },
            TestContext.Current.CancellationToken);

        commandResponse.EnsureSuccessStatusCode();
        Assert.Equal(
            new DashboardExecuteCommandRequest("db-connection-string", "set-parameter"),
            commandExecutor.Request);
    }

    [Fact]
    public async Task GetResources_ReturnsSourceGeneratedSnapshotFromVersionOneRoute()
    {
        DashboardResource[] resources =
        [
            new DashboardResource(
                "api-abc123",
                "Project",
                "api",
                "resource-1",
                "Running",
                "success",
                "Healthy",
                DateTime.Parse("2026-07-13T12:00:00Z"),
                DateTime.Parse("2026-07-13T12:00:01Z"),
                null,
                [new("http", "https://api.example.test", false, false, "API", 1)],
                [new("project.path", "Project path", "/src/api.csproj", false, true, 10)],
                [new("ASPNETCORE_ENVIRONMENT", "Development", true)],
                [new("Healthy", "ready", "Ready")],
                [new("restart", "Restart", "Restart API", null, "ArrowCounterclockwise", "regular", true, "enabled")],
                [new("postgres", "Reference")],
                false,
                true,
                "Code",
                "filled",
                false,
                null)
        ];

        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardResourceSnapshotProvider>(new TestResourceSnapshotProvider(resources));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/v1/resources", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var resource = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("api-abc123", resource.GetProperty("name").GetString());
        Assert.Equal("https://api.example.test", resource.GetProperty("urls")[0].GetProperty("url").GetString());
        Assert.Equal("/src/api.csproj", resource.GetProperty("properties")[0].GetProperty("value").GetString());
        Assert.Equal("enabled", resource.GetProperty("commands")[0].GetProperty("state").GetString());
        Assert.Equal(22, resource.EnumerateObject().Count());
    }

    [Fact]
    public async Task GetResources_ReturnsServiceUnavailableWithoutResourceServiceConfiguration()
    {
        await using var app = DashboardBackendApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/v1/resources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResources_ReturnsServiceUnavailableWhenConfiguredResourceServiceCannotConnect()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)socket.LocalEndPoint!;

        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = $"http://127.0.0.1:{endpoint.Port}",
                ["DashboardBackend:InitialSnapshotTimeout"] = "00:00:01"
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/dashboard/v1/resources", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("will keep retrying", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteCommand_ReturnsSourceGeneratedResponseFromVersionOneRoute()
    {
        var result = new DashboardCommandResponse(
            "succeeded",
            "Restarted",
            new DashboardCommandResult("done", "text", true));
        var executor = new TestCommandExecutor(result);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardCommandExecutor>(executor);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/dashboard/v1/commands/execute",
            new { resourceName = "api", commandName = "restart" },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(new DashboardExecuteCommandRequest("api", "restart"), executor.Request);
        Assert.Equal(
            "{\"kind\":\"succeeded\",\"message\":\"Restarted\",\"result\":{\"value\":\"done\",\"format\":\"text\",\"displayImmediately\":true}}",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"resourceName\":\"api\",\"commandName\":\"\"}")]
    public async Task ExecuteCommand_RejectsInvalidRequests(string content)
    {
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardCommandExecutor>(new TestCommandExecutor(null));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.PostAsync(
            "/api/dashboard/v1/commands/execute",
            new StringContent(content, System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ExecuteCommand_ReturnsNotFoundForUnknownCommand()
    {
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardCommandExecutor>(new TestCommandExecutor(null));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/dashboard/v1/commands/execute",
            new { resourceName = "api", commandName = "missing" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CommandExecutor_UsesSharedResourceServiceConnection()
    {
        var connection = new TestResourceServiceConnection(isConfigured: true)
        {
            CommandResponse = new ResourceCommandResponse
            {
                Kind = ResourceCommandResponseKind.Succeeded,
                Message = "Restarted"
            }
        };
        DashboardResource[] resources =
        [
            new DashboardResource(
                "api",
                "Project",
                "API",
                "resource-1",
                "Running",
                "success",
                "Healthy",
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                [new("restart", "Restart", null, null, null, "regular", false, "enabled")],
                [],
                false,
                true,
                null,
                null,
                false,
                null)
        ];
        var executor = new DashboardCommandExecutor(
            connection,
            new TestResourceSnapshotProvider(resources),
            NullLogger<DashboardCommandExecutor>.Instance);

        var response = await executor.ExecuteAsync(
            new DashboardExecuteCommandRequest("api", "restart"),
            TestContext.Current.CancellationToken);

        Assert.Equal("succeeded", response?.Kind);
        Assert.Equal("Restarted", response?.Message);
        Assert.Equal("api", connection.CommandRequest?.ResourceName);
        Assert.Equal("Project", connection.CommandRequest?.ResourceType);
        Assert.Equal("restart", connection.CommandRequest?.CommandName);
    }

    [Fact]
    public async Task InteractionService_PreservesOrderingUpdatesAndFailedDeliveryRetry()
    {
        var connection = new TestResourceServiceConnection(isConfigured: true);
        using var service = new DashboardInteractionService(
            connection,
            NullLogger<DashboardInteractionService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);

        await connection.InteractionUpdates.Writer.WriteAsync(
            new WatchInteractionsResponseUpdate
            {
                InteractionId = 7,
                Title = "Deployment complete",
                Notification = new InteractionNotification
                {
                    Intent = MessageIntent.Success,
                    LinkText = "Open",
                    LinkUrl = "https://example.test"
                }
            },
            TestContext.Current.CancellationToken);
        var inputs = new WatchInteractionsResponseUpdate
        {
            InteractionId = 8,
            Title = "Set parameter",
            InputsDialog = new InteractionInputsDialog()
        };
        inputs.InputsDialog.InputItems.Add(new InteractionInput
        {
            Name = "value",
            Label = "Value",
            Value = "initial",
            InputType = InputType.Text,
            UpdateStateOnChange = true
        });
        await connection.InteractionUpdates.Writer.WriteAsync(
            inputs,
            TestContext.Current.CancellationToken);

        await WaitUntilAsync(() => service.GetInteractions().Length is 2);
        Assert.Collection(
            service.GetInteractions(),
            interaction =>
            {
                Assert.Equal(7, interaction.InteractionId);
                Assert.Equal("notification", interaction.Kind);
            },
            interaction =>
            {
                Assert.Equal(8, interaction.InteractionId);
                Assert.Equal("inputsDialog", interaction.Kind);
            });

        Assert.True(await service.RespondAsync(
            new DashboardRespondInteractionRequest(
                8,
                "update",
                new Dictionary<string, string> { ["value"] = "updated" }),
            TestContext.Current.CancellationToken));
        var updateResponse = await connection.InteractionResponses.Reader.ReadAsync(
            TestContext.Current.CancellationToken);
        Assert.True(updateResponse.ResponseUpdate);
        Assert.Equal("updated", Assert.Single(updateResponse.InputsDialog.InputItems).Value);

        var validationUpdate = inputs.Clone();
        validationUpdate.InputsDialog.InputItems[0].Value = "updated";
        validationUpdate.InputsDialog.InputItems[0].ValidationErrors.Add("Value is unavailable.");
        await connection.InteractionUpdates.Writer.WriteAsync(
            validationUpdate,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() =>
            service.GetInteractions().Single(interaction => interaction.InteractionId == 8)
                .Inputs[0].ValidationErrors.Length is 1);
        Assert.Equal(
            "Value is unavailable.",
            service.GetInteractions().Single(interaction => interaction.InteractionId == 8)
                .Inputs[0].ValidationErrors[0]);

        connection.FailNextInteractionResponse();
        Assert.True(await service.RespondAsync(
            new DashboardRespondInteractionRequest(7, "primary", null),
            TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => service.GetInteractions().Any(interaction => interaction.InteractionId == 7));
        Assert.Equal(
            [7, 8],
            service.GetInteractions().Select(interaction => interaction.InteractionId).ToArray());

        Assert.True(await service.RespondAsync(
            new DashboardRespondInteractionRequest(
                8,
                "submit",
                new Dictionary<string, string> { ["value"] = "final" }),
            TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => service.GetInteractions().All(interaction => interaction.InteractionId != 8));
        var submitResponse = await connection.InteractionResponses.Reader.ReadAsync(
            TestContext.Current.CancellationToken);
        Assert.False(submitResponse.ResponseUpdate);
        Assert.Equal("final", Assert.Single(submitResponse.InputsDialog.InputItems).Value);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InputProducingCommand_AndResponseUseSameResourceServiceSession()
    {
        var connection = new TestResourceServiceConnection(isConfigured: true);
        connection.CommandHandler = async (request, cancellationToken) =>
        {
            var interaction = new WatchInteractionsResponseUpdate
            {
                InteractionId = 19,
                Title = "Set parameter",
                InputsDialog = new InteractionInputsDialog()
            };
            interaction.InputsDialog.InputItems.Add(new InteractionInput
            {
                Name = "value",
                Label = "Value",
                InputType = InputType.SecretText,
                Required = true
            });
            await connection.InteractionUpdates.Writer.WriteAsync(interaction, cancellationToken);

            var response = await connection.InteractionResponses.Reader.ReadAsync(cancellationToken);
            Assert.Equal(19, response.InteractionId);
            Assert.Equal("new-secret", Assert.Single(response.InputsDialog.InputItems).Value);
            return new ResourceCommandResponse { Kind = ResourceCommandResponseKind.Succeeded };
        };

        using var interactionService = new DashboardInteractionService(
            connection,
            NullLogger<DashboardInteractionService>.Instance);
        await interactionService.StartAsync(TestContext.Current.CancellationToken);
        var executor = new DashboardCommandExecutor(
            connection,
            new TestResourceSnapshotProvider(
            [
                new DashboardResource(
                    "parameter",
                    "Parameter",
                    "parameter",
                    "resource-1",
                    "ValueMissing",
                    "warning",
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    [],
                    [new("set-parameter", "Set value", null, null, null, "regular", false, "enabled")],
                    [],
                    false,
                    true,
                    null,
                    null,
                    false,
                    null)
            ]),
            NullLogger<DashboardCommandExecutor>.Instance);

        var commandTask = executor.ExecuteAsync(
            new DashboardExecuteCommandRequest("parameter", "set-parameter"),
            TestContext.Current.CancellationToken).AsTask();
        await WaitUntilAsync(() => interactionService.GetInteractions().Length is 1);
        var prompt = Assert.Single(interactionService.GetInteractions());
        Assert.Equal("secretText", Assert.Single(prompt.Inputs).InputType);

        Assert.True(await interactionService.RespondAsync(
            new DashboardRespondInteractionRequest(
                19,
                "submit",
                new Dictionary<string, string> { ["value"] = "new-secret" }),
            TestContext.Current.CancellationToken));

        var commandResponse = await commandTask;
        Assert.NotNull(commandResponse);
        Assert.Equal("succeeded", commandResponse.Kind);
        await interactionService.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResourceSnapshot_RecoversAfterInitialConnectionFailure()
    {
        var service = new DashboardResourceSnapshotService(
            new TestResourceServiceConnection(),
            new ConfigurationBuilder().Build(),
            NullLogger<DashboardResourceSnapshotService>.Instance);
        service.ReportInitialFailure("Unavailable");

        var exception = await Assert.ThrowsAsync<DashboardResourceServiceUnavailableException>(async () =>
            await service.GetSnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Unavailable", exception.Message);

        var initialUpdate = new WatchResourcesUpdate
        {
            InitialData = new InitialResourceData()
        };
        initialUpdate.InitialData.Resources.Add(new ProtoResource
        {
            Name = "api",
            DisplayName = "API",
            ResourceType = "Project",
            Uid = "resource-1"
        });
        service.ApplyUpdate(initialUpdate);

        Assert.Equal("api", Assert.Single(await service.GetSnapshotAsync(TestContext.Current.CancellationToken)).Name);
    }

    [Fact]
    public void ResourceMapper_ProjectsResourceServiceContractWithoutDashboardDependencies()
    {
        var resource = new ProtoResource
        {
            Name = "worker-xyz",
            ResourceType = "Executable",
            DisplayName = "worker",
            Uid = "resource-2",
            State = "Running",
            StateStyle = "success",
            CreatedAt = Timestamp.FromDateTime(DateTime.Parse("2026-07-13T12:00:00Z").ToUniversalTime()),
            StartedAt = Timestamp.FromDateTime(DateTime.Parse("2026-07-13T12:00:01Z").ToUniversalTime()),
            SupportsDetailedTelemetry = true,
            IconName = "WindowConsole",
            IconVariant = IconVariant.Filled
        };
        resource.Urls.Add(new Url
        {
            EndpointName = "http",
            FullUrl = "http://localhost:5000",
            DisplayProperties = new UrlDisplayProperties { DisplayName = "HTTP", SortOrder = 2 }
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "terminal.enabled",
            Value = Value.ForString("true")
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "terminal.replicaIndex",
            Value = Value.ForString("3")
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "resource.state",
            Value = Value.ForString("Running")
        });
        resource.Properties.Add(new ResourceProperty
        {
            Name = "executable.pid",
            DisplayName = "Process ID",
            Value = Value.ForString("123"),
            SortOrder = 2
        });
        resource.HealthReports.Add(new HealthReport
        {
            Key = "live",
            Status = HealthStatus.Degraded,
            Description = "Slow"
        });
        resource.Commands.Add(new ResourceCommand
        {
            Name = "restart",
            DisplayName = "Restart",
            IconVariant = IconVariant.Regular,
            State = ResourceCommandState.Disabled
        });

        var result = DashboardResourceSnapshotService.Map(resource);

        Assert.Equal("worker", result.DisplayName);
        Assert.Equal("Degraded", result.Health);
        Assert.True(result.HasTerminal);
        Assert.Equal(3, result.TerminalReplicaIndex);
        Assert.Equal("filled", result.IconVariant);
        Assert.Equal("disabled", Assert.Single(result.Commands).State);
        Assert.Equal("HTTP", Assert.Single(result.Urls).DisplayName);
        Assert.Equal("http://localhost:5000/", Assert.Single(result.Urls).Url);
        Assert.Collection(
            result.Properties,
            property =>
            {
                Assert.Equal("resource.state", property.Name);
                Assert.Equal("State", property.DisplayName);
                Assert.Equal(1, property.SortOrder);
            },
            property =>
            {
                Assert.Equal("executable.pid", property.Name);
                Assert.Equal(9, property.SortOrder);
            },
            property => Assert.Equal("terminal.enabled", property.Name),
            property => Assert.Equal("terminal.replicaIndex", property.Name));
    }

    [Fact]
    public async Task ResourceEventSource_SendsSnapshotBeforeIncrementalChanges()
    {
        var service = new DashboardResourceSnapshotService(
            new TestResourceServiceConnection(),
            new ConfigurationBuilder().Build(),
            NullLogger<DashboardResourceSnapshotService>.Instance);
        var initialResource = new ProtoResource
        {
            Name = "api",
            DisplayName = "API",
            ResourceType = "Project",
            Uid = "resource-1",
            State = "Starting"
        };
        var initialUpdate = new WatchResourcesUpdate
        {
            InitialData = new InitialResourceData()
        };
        initialUpdate.InitialData.Resources.Add(initialResource);
        service.ApplyUpdate(initialUpdate);

        await using var events = service
            .WatchAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await events.MoveNextAsync());
        var snapshot = events.Current;
        Assert.Equal("snapshot", snapshot.Type);
        Assert.Equal("Starting", Assert.Single(snapshot.Resources!).State);
        Assert.Null(snapshot.Upserts);
        Assert.Null(snapshot.Deletes);

        var updatedResource = initialResource.Clone();
        updatedResource.State = "Running";
        var changes = new WatchResourcesUpdate
        {
            Changes = new WatchResourcesChanges()
        };
        changes.Changes.Value.Add(new WatchResourcesChange { Upsert = updatedResource });
        changes.Changes.Value.Add(new WatchResourcesChange
        {
            Delete = new ResourceDeletion { ResourceName = "worker", ResourceType = "Project" }
        });
        service.ApplyUpdate(changes);

        Assert.True(await events.MoveNextAsync());
        var change = events.Current;
        Assert.Equal("change", change.Type);
        Assert.Equal("Running", Assert.Single(change.Upserts!).State);
        Assert.Equal("worker", Assert.Single(change.Deletes!));
        Assert.Null(change.Resources);
    }

    [Fact]
    public async Task ResourceHub_StreamsSourceGeneratedSnapshotAndChanges()
    {
        DashboardResource[] resources =
        [
            new DashboardResource(
                "api",
                "Project",
                "API",
                "resource-1",
                "Running",
                "success",
                "Healthy",
                null,
                null,
                null,
                [],
                [],
                [],
                [],
                [],
                [],
                false,
                true,
                "Code",
                "regular",
                false,
                null)
        ];
        DashboardResourcesEvent[] resourceEvents =
        [
            DashboardResourcesEvent.Snapshot(resources),
            DashboardResourcesEvent.Change(resources, ["worker"])
        ];

        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardResourceEventSource>(new TestResourceEventSource(resourceEvents));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{DashboardApiContract.ResourceStreamPath}", options =>
            {
                options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DashboardBackendJsonSerializerContext.Default);
            })
            .Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);

        await using var events = connection
            .StreamAsync<DashboardResourcesEvent>(nameof(DashboardResourcesHub.WatchResources), TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("snapshot", events.Current.Type);
        Assert.Equal("api", Assert.Single(events.Current.Resources!).Name);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("change", events.Current.Type);
        Assert.Equal("api", Assert.Single(events.Current.Upserts!).Name);
        Assert.Equal("worker", Assert.Single(events.Current.Deletes!));
    }

    [Fact]
    public async Task GetStructuredLogs_ReturnsSourceGeneratedBacklogAndForwardsCredentials()
    {
        var source = new TestStructuredLogSource(
            new DashboardStructuredLogsSnapshot(
                2,
                JsonSerializer.SerializeToElement(new { resourceLogs = new[] { new { resource = new { } } } })),
            []);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardStructuredLogSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/v1/structured-logs");
        request.Headers.TryAddWithoutValidation("Cookie", ".Aspire.Dashboard=browser-session");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer dashboard-token");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            "{\"totalCount\":2,\"data\":{\"resourceLogs\":[{\"resource\":{}}]}}",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(".Aspire.Dashboard=browser-session", source.Credentials?.Cookie);
        Assert.Equal("Bearer dashboard-token", source.Credentials?.Authorization);
    }

    [Fact]
    public async Task StructuredLogHub_StreamsSourceGeneratedOtlpEvents()
    {
        DashboardStructuredLogsEvent[] logEvents =
        [
            new(JsonSerializer.SerializeToElement(new
            {
                resourceLogs = new[]
                {
                    new { scopeLogs = new[] { new { logRecords = new[] { new { body = new { stringValue = "started" } } } } } }
                }
            }))
        ];
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardStructuredLogSource>(new TestStructuredLogSource(
                new DashboardStructuredLogsSnapshot(0, JsonSerializer.SerializeToElement(new { })),
                logEvents));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{DashboardApiContract.StructuredLogStreamPath}", options =>
            {
                options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DashboardBackendJsonSerializerContext.Default);
            })
            .Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);

        await using var events = connection
            .StreamAsync<DashboardStructuredLogsEvent>(nameof(DashboardStructuredLogsHub.WatchStructuredLogs), TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(
            "started",
            events.Current.Data.GetProperty("resourceLogs")[0]
                .GetProperty("scopeLogs")[0]
                .GetProperty("logRecords")[0]
                .GetProperty("body")
                .GetProperty("stringValue")
                .GetString());
    }

    [Fact]
    public async Task GetTraces_ReturnsSourceGeneratedFilteredSnapshotAndForwardsCredentials()
    {
        var source = new TestTraceSource(
            new DashboardTraceSnapshot(
                12,
                1,
                JsonSerializer.SerializeToElement(new
                {
                    resourceSpans = new[]
                    {
                        new
                        {
                            scopeSpans = new[]
                            {
                                new
                                {
                                    spans = new[]
                                    {
                                        new { traceId = "trace-1", spanId = "span-1", name = "catalog" }
                                    }
                                }
                            }
                        }
                    }
                })),
            []);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardTraceSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/dashboard/v1/traces?resource=api&resource=worker&traceId=abc&hasError=true&limit=25&search=status%3Aerror");
        request.Headers.TryAddWithoutValidation("Cookie", ".Aspire.Dashboard=browser-session");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer dashboard-token");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            "{\"totalCount\":12,\"returnedCount\":1,\"data\":{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"trace-1\",\"spanId\":\"span-1\",\"name\":\"catalog\"}]}]}]}}",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(source.Query);
        Assert.Equal(["api", "worker"], source.Query.ResourceNames);
        Assert.Equal("abc", source.Query.TraceId);
        Assert.True(source.Query.HasError);
        Assert.Equal(25, source.Query.Limit);
        Assert.Equal("status:error", source.Query.Search);
        Assert.Equal(".Aspire.Dashboard=browser-session", source.Credentials?.Cookie);
        Assert.Equal("Bearer dashboard-token", source.Credentials?.Authorization);
    }

    [Theory]
    [InlineData("/api/dashboard/v1/traces?hasError=not-a-boolean")]
    [InlineData("/api/dashboard/v1/traces?limit=-1")]
    [InlineData("/api/dashboard/v1/traces?limit=1.5")]
    public async Task GetTraces_RejectsInvalidFilters(string path)
    {
        var source = new TestTraceSource(
            new DashboardTraceSnapshot(0, 0, JsonSerializer.SerializeToElement(new { })),
            []);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardTraceSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(source.Query);
    }

    [Fact]
    public async Task ClearTraces_ForwardsResourceAndCredentials()
    {
        var source = new TestTraceSource(
            new DashboardTraceSnapshot(0, 0, JsonSerializer.SerializeToElement(new { })),
            []);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardTraceSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/dashboard/v1/traces?resource=api");
        request.Headers.TryAddWithoutValidation("Cookie", ".Aspire.Dashboard=browser-session");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("api", source.ClearedResourceName);
        Assert.Equal(".Aspire.Dashboard=browser-session", source.Credentials?.Cookie);
    }

    [Fact]
    public async Task TraceHub_StreamsBacklogBeforeLiveWithFilters()
    {
        DashboardTraceEvent[] traceEvents =
        [
            new(JsonSerializer.SerializeToElement(new
            {
                resourceSpans = new[]
                {
                    new { scopeSpans = new[] { new { spans = new[] { new { name = "backlog" } } } } }
                }
            })),
            new(JsonSerializer.SerializeToElement(new
            {
                resourceSpans = new[]
                {
                    new { scopeSpans = new[] { new { spans = new[] { new { name = "live" } } } } }
                }
            }))
        ];
        var source = new TestTraceSource(
            new DashboardTraceSnapshot(0, 0, JsonSerializer.SerializeToElement(new { })),
            traceEvents);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardTraceSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{DashboardApiContract.TraceStreamPath}", options =>
            {
                options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add("Cookie", ".Aspire.Dashboard=browser-session");
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DashboardBackendJsonSerializerContext.Default);
            })
            .Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);

        await using var events = connection
            .StreamAsync<DashboardTraceEvent>(
                nameof(DashboardTracesHub.WatchTraces),
                new DashboardTraceStreamRequest(["api"], "abc", true, "status:error"),
                TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(
            "backlog",
            events.Current.Data.GetProperty("resourceSpans")[0]
                .GetProperty("scopeSpans")[0]
                .GetProperty("spans")[0]
                .GetProperty("name")
                .GetString());
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(
            "live",
            events.Current.Data.GetProperty("resourceSpans")[0]
                .GetProperty("scopeSpans")[0]
                .GetProperty("spans")[0]
                .GetProperty("name")
                .GetString());
        Assert.NotNull(source.Query);
        Assert.Equal(["api"], source.Query.ResourceNames);
        Assert.Equal("abc", source.Query.TraceId);
        Assert.True(source.Query.HasError);
        Assert.Equal("status:error", source.Query.Search);
        Assert.Equal(".Aspire.Dashboard=browser-session", source.Credentials?.Cookie);
    }

    [Fact]
    public async Task GetMetrics_ReturnsSourceGeneratedSummariesAndForwardsCredentials()
    {
        var source = new TestMetricSource(
            [
                new DashboardMetricSummary(
                    "http.server.request.duration",
                    "Server request duration.",
                    "ms",
                    "api",
                    "OpenTelemetry.Instrumentation.AspNetCore",
                    "histogram",
                    42,
                    17)
            ],
            series: null);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardMetricSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/v1/metrics");
        request.Headers.TryAddWithoutValidation("Cookie", ".Aspire.Dashboard=browser-session");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer dashboard-token");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            "[{\"name\":\"http.server.request.duration\",\"description\":\"Server request duration.\",\"unit\":\"ms\",\"resourceName\":\"api\",\"meterName\":\"OpenTelemetry.Instrumentation.AspNetCore\",\"kind\":\"histogram\",\"lastValue\":42,\"pointCount\":17}]",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(".Aspire.Dashboard=browser-session", source.Credentials?.Cookie);
        Assert.Equal("Bearer dashboard-token", source.Credentials?.Authorization);
    }

    [Fact]
    public async Task GetMetricSeries_PreservesDimensionsHistogramAndExemplars()
    {
        var source = new TestMetricSource(
            [],
            new DashboardMetricSeriesResponse(
                "http.server.request.duration",
                "api",
                "OpenTelemetry.Instrumentation.AspNetCore",
                "ms",
                "histogram",
                [1000, 2000],
                null,
                null,
                null,
                null,
                [30, 40],
                [25, 50],
                [
                    new DashboardMetricBucketSeries(25, [1, 2]),
                    new DashboardMetricBucketSeries(null, [0, 1])
                ],
                [
                    new DashboardMetricDimensionFilter("http.method", ["GET", null]),
                    new DashboardMetricDimensionFilter("route", [])
                ],
                [
                    new DashboardMetricDimensionSeries(
                        [new DashboardMetricAttribute("http.method", "GET")],
                        [1000, 2000],
                        null,
                        null,
                        null,
                        null,
                        [30, 40],
                        [new DashboardMetricBucketSeries(null, [1, 1])])
                ],
                [
                    new DashboardMetricExemplar(
                        1500,
                        37,
                        "11111111111111111111111111111111",
                        "2222222222222222",
                        [new DashboardMetricAttribute("request.id", "abc")])
                ],
                true,
                false,
                "buckets"));
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardMetricSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/dashboard/v1/metrics/series?resource=api&meter=OpenTelemetry.Instrumentation.AspNetCore"
            + "&instrument=http.server.request.duration&windowSeconds=60&maxPoints=20&showCount=false"
            + "&histogramMode=buckets&dimension.http.method=s%3AGET&dimension.http.method=n%3A"
            + "&dimension.route=x%3A");
        request.Headers.TryAddWithoutValidation("Cookie", ".Aspire.Dashboard=browser-session");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("buckets", root.GetProperty("histogramMode").GetString());
        Assert.True(root.GetProperty("hasOverflow").GetBoolean());
        Assert.Equal(2, root.GetProperty("bucketBounds").GetArrayLength());
        Assert.Equal(2, root.GetProperty("buckets").GetArrayLength());
        Assert.Equal("GET", root.GetProperty("dimensionFilters")[0].GetProperty("values")[0].GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("dimensionFilters")[0].GetProperty("values")[1].ValueKind);
        Assert.Equal("11111111111111111111111111111111", root.GetProperty("exemplars")[0].GetProperty("traceId").GetString());

        Assert.NotNull(source.Query);
        Assert.Equal("api", source.Query.ResourceName);
        Assert.Equal("OpenTelemetry.Instrumentation.AspNetCore", source.Query.MeterName);
        Assert.Equal("http.server.request.duration", source.Query.InstrumentName);
        Assert.Equal(60, source.Query.WindowSeconds);
        Assert.Equal(20, source.Query.MaxPoints);
        Assert.False(source.Query.ShowCount);
        Assert.Equal("buckets", source.Query.HistogramMode);
        Assert.Collection(
            source.Query.Dimensions["http.method"],
            value => Assert.Equal("GET", value),
            Assert.Null);
        Assert.Empty(source.Query.Dimensions["route"]);
        Assert.Equal(".Aspire.Dashboard=browser-session", source.Credentials?.Cookie);
    }

    [Theory]
    [InlineData("/api/dashboard/v1/metrics/series")]
    [InlineData("/api/dashboard/v1/metrics/series?resource=api&meter=m&instrument=i&windowSeconds=nope")]
    [InlineData("/api/dashboard/v1/metrics/series?resource=api&meter=m&instrument=i&showCount=nope")]
    [InlineData("/api/dashboard/v1/metrics/series?resource=api&meter=m&instrument=i&histogramMode=average")]
    [InlineData("/api/dashboard/v1/metrics/series?resource=api&meter=m&instrument=i&dimension.name=invalid")]
    public async Task GetMetricSeries_RejectsInvalidQueries(string path)
    {
        var source = new TestMetricSource([], series: null);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardMetricSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync(
            path,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(source.Query);
    }

    [Fact]
    public async Task ClearMetrics_ForwardsResourceAndCredentials()
    {
        var source = new TestMetricSource([], series: null);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardMetricSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            "/api/dashboard/v1/metrics?resource=api");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer dashboard-token");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("api", source.ClearedResourceName);
        Assert.Equal("Bearer dashboard-token", source.Credentials?.Authorization);
    }

    [Fact]
    public async Task ConsoleLogHub_StreamsResourceScopedBacklogAndLiveLines()
    {
        DashboardConsoleLogsEvent[] logEvents =
        [
            new("api", [new(1, "backlog", false), new(2, "warning", true)]),
            new("api", [new(3, "live", false)])
        ];
        var source = new TestConsoleLogSource(logEvents);
        await using var app = DashboardBackendApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IDashboardConsoleLogSource>(source);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        await using var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost{DashboardApiContract.ConsoleLogStreamPath}", options =>
            {
                options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers.Add("Cookie", ".Aspire.Dashboard=browser-session");
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DashboardBackendJsonSerializerContext.Default);
            })
            .Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);

        await using var events = connection
            .StreamAsync<DashboardConsoleLogsEvent>(
                nameof(DashboardConsoleLogsHub.WatchConsoleLogs),
                "api",
                TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("backlog", events.Current.Lines[0].Text);
        Assert.True(events.Current.Lines[1].IsStdErr);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(3, Assert.Single(events.Current.Lines).LineNumber);
        Assert.Equal("api", source.ResourceName);
        Assert.Equal(".Aspire.Dashboard=browser-session", source.Credentials?.Cookie);
    }

    private sealed class TestResourceSnapshotProvider(DashboardResource[] resources) : IDashboardResourceSnapshotProvider
    {
        public ValueTask<DashboardResource[]> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(resources);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = TimeSpan.FromSeconds(5);
        var startedAt = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - startedAt > timeout)
            {
                throw new TimeoutException($"Condition was not met within {timeout}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }

    private sealed class TestFrontendAssetProvider(IReadOnlyDictionary<string, string> assets) : IDashboardFrontendAssetProvider
    {
        public Stream? Open(string path)
        {
            return assets.TryGetValue(path, out var content)
                ? new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))
                : null;
        }
    }

    private sealed class TestResourceEventSource(DashboardResourcesEvent[] events) : IDashboardResourceEventSource
    {
        public async IAsyncEnumerable<DashboardResourcesEvent> WatchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var resourceEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return resourceEvent;
                await Task.Yield();
            }
        }
    }

    private sealed class TestCommandExecutor(DashboardCommandResponse? response) : IDashboardCommandExecutor
    {
        public DashboardExecuteCommandRequest? Request { get; private set; }

        public ValueTask<DashboardCommandResponse?> ExecuteAsync(
            DashboardExecuteCommandRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(response);
        }
    }

    private sealed class TestInteractionService(DashboardInteraction[] interactions) : IDashboardInteractionService
    {
        public DashboardRespondInteractionRequest? Request { get; private set; }

        public DashboardInteraction[] GetInteractions() => interactions;

        public ValueTask<bool> RespondAsync(
            DashboardRespondInteractionRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(interactions.Any(interaction => interaction.InteractionId == request.InteractionId));
        }
    }

    private sealed class TestResourceServiceConnection(bool isConfigured = false) : IDashboardResourceServiceConnection
    {
        private int _failNextInteractionResponse;

        public Channel<WatchResourcesUpdate> ResourceUpdates { get; } = Channel.CreateUnbounded<WatchResourcesUpdate>();
        public Channel<WatchInteractionsResponseUpdate> InteractionUpdates { get; } = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        public Channel<WatchInteractionsRequestUpdate> InteractionResponses { get; } = Channel.CreateUnbounded<WatchInteractionsRequestUpdate>();
        public ResourceCommandRequest? CommandRequest { get; private set; }
        public Func<ResourceCommandRequest, CancellationToken, ValueTask<ResourceCommandResponse>>? CommandHandler { get; set; }
        public ResourceCommandResponse CommandResponse { get; set; } = new()
        {
            Kind = ResourceCommandResponseKind.Succeeded
        };
        public bool IsConfigured { get; } = isConfigured;
        public string UnavailableMessage => "Test resource service is unavailable.";

        public async IAsyncEnumerable<WatchResourcesUpdate> WatchResourcesAsync(
            bool isReconnect,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var update in ResourceUpdates.Reader.ReadAllAsync(cancellationToken))
            {
                yield return update;
            }
        }

        public ValueTask<ResourceCommandResponse> ExecuteResourceCommandAsync(
            ResourceCommandRequest request,
            CancellationToken cancellationToken)
        {
            CommandRequest = request;
            return CommandHandler is null
                ? ValueTask.FromResult(CommandResponse)
                : CommandHandler(request, cancellationToken);
        }

        public async Task RunInteractionSessionAsync(
            ChannelReader<DashboardPendingInteractionResponse> responses,
            Func<WatchInteractionsResponseUpdate, ValueTask> onUpdate,
            CancellationToken cancellationToken)
        {
            var responseTask = WriteResponsesAsync();
            try
            {
                await foreach (var update in InteractionUpdates.Reader.ReadAllAsync(cancellationToken))
                {
                    await onUpdate(update);
                }
            }
            finally
            {
                try
                {
                    await responseTask;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }

            async Task WriteResponsesAsync()
            {
                await foreach (var response in responses.ReadAllAsync(cancellationToken))
                {
                    if (Interlocked.Exchange(ref _failNextInteractionResponse, 0) is 1)
                    {
                        response.MarkFailed(new IOException("Simulated interaction transport failure."));
                    }
                    else
                    {
                        await InteractionResponses.Writer.WriteAsync(response.Request, cancellationToken);
                        response.MarkDelivered();
                    }
                }
            }
        }

        public void FailNextInteractionResponse() => Interlocked.Exchange(ref _failNextInteractionResponse, 1);
    }

    private sealed class TestStructuredLogSource(
        DashboardStructuredLogsSnapshot snapshot,
        DashboardStructuredLogsEvent[] events) : IDashboardStructuredLogSource
    {
        public DashboardRequestCredentials? Credentials { get; private set; }

        public ValueTask<DashboardStructuredLogsSnapshot> GetSnapshotAsync(
            DashboardRequestCredentials credentials,
            CancellationToken cancellationToken)
        {
            Credentials = credentials;
            return ValueTask.FromResult(snapshot);
        }

        public async IAsyncEnumerable<DashboardStructuredLogsEvent> WatchAsync(
            DashboardRequestCredentials credentials,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Credentials = credentials;
            foreach (var logEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return logEvent;
                await Task.Yield();
            }
        }
    }

    private sealed class TestTraceSource(
        DashboardTraceSnapshot snapshot,
        DashboardTraceEvent[] events) : IDashboardTraceSource
    {
        public DashboardTraceQuery? Query { get; private set; }
        public DashboardRequestCredentials? Credentials { get; private set; }
        public string? ClearedResourceName { get; private set; }

        public ValueTask<DashboardTraceSnapshot?> GetSnapshotAsync(
            DashboardTraceQuery query,
            DashboardRequestCredentials credentials,
            CancellationToken cancellationToken)
        {
            Query = query;
            Credentials = credentials;
            return ValueTask.FromResult<DashboardTraceSnapshot?>(snapshot);
        }

        public async IAsyncEnumerable<DashboardTraceEvent> WatchAsync(
            DashboardTraceQuery query,
            DashboardRequestCredentials credentials,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Query = query;
            Credentials = credentials;
            foreach (var traceEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return traceEvent;
                await Task.Yield();
            }
        }

        public ValueTask<bool> ClearAsync(
            string? resourceName,
            DashboardRequestCredentials credentials,
            CancellationToken cancellationToken)
        {
            ClearedResourceName = resourceName;
            Credentials = credentials;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class TestMetricSource(
        DashboardMetricSummary[] summaries,
        DashboardMetricSeriesResponse? series) : IDashboardMetricSource
    {
        public DashboardMetricSeriesQuery? Query { get; private set; }
        public DashboardRequestCredentials? Credentials { get; private set; }
        public string? ClearedResourceName { get; private set; }

        public ValueTask<DashboardMetricSummary[]> GetSummariesAsync(
            DashboardRequestCredentials credentials,
            CancellationToken cancellationToken)
        {
            Credentials = credentials;
            return ValueTask.FromResult(summaries);
        }

        public ValueTask<DashboardMetricSeriesResponse?> GetSeriesAsync(
            DashboardMetricSeriesQuery query,
            DashboardRequestCredentials credentials,
            CancellationToken cancellationToken)
        {
            Query = query;
            Credentials = credentials;
            return ValueTask.FromResult(series);
        }

        public ValueTask<bool> ClearAsync(
            string? resourceName,
            DashboardRequestCredentials credentials,
            CancellationToken cancellationToken)
        {
            ClearedResourceName = resourceName;
            Credentials = credentials;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class TestConsoleLogSource(DashboardConsoleLogsEvent[] events) : IDashboardConsoleLogSource
    {
        public string? ResourceName { get; private set; }
        public DashboardRequestCredentials? Credentials { get; private set; }

        public async IAsyncEnumerable<DashboardConsoleLogsEvent> WatchAsync(
            string resourceName,
            DashboardRequestCredentials credentials,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ResourceName = resourceName;
            Credentials = credentials;
            foreach (var logEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return logEvent;
                await Task.Yield();
            }
        }
    }

}
