// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Diagnostics;
using Aspire.Hosting.Utils;
using Aspire.Tests;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Backchannel;

[Trait("Partition", "4")]
public class AuxiliaryBackchannelRpcTargetTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task GetAppHostInfoAsync_ReturnsAssemblyDisplayVersion()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Path"] = "/path/to/apphost.csproj"
            })
            .Build();

        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<ProfilingTelemetry>()
            .BuildServiceProvider();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            configuration,
            services.GetRequiredService<ProfilingTelemetry>(),
            services);

        var result = await target.GetAppHostInfoAsync().DefaultTimeout();
        var expectedVersion = typeof(AuxiliaryBackchannelRpcTarget).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plusIndex = expectedVersion?.IndexOf('+') ?? -1;
        if (plusIndex > 0)
        {
            expectedVersion = expectedVersion![..plusIndex];
        }
        expectedVersion ??= typeof(AuxiliaryBackchannelRpcTarget).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? typeof(AuxiliaryBackchannelRpcTarget).Assembly.GetCustomAttribute<AssemblyVersionAttribute>()?.Version
            ?? "unknown";

        Assert.Equal(expectedVersion, result.AspireHostVersion);
    }

    [Fact]
    public void AppHostStartupState_IsReadyWhenRemoteAppHostSocketIsAbsent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var startupState = new AppHostStartupState(configuration);

        Assert.True(startupState.IsReady);
    }

    [Fact]
    public void AppHostStartupState_WaitsForReadyWhenRemoteAppHostSocketIsPresent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REMOTE_APP_HOST_SOCKET_PATH"] = "aspire-remote.sock"
            })
            .Build();
        var startupState = new AppHostStartupState(configuration);

        Assert.False(startupState.IsReady);

        startupState.MarkReady();

        Assert.True(startupState.IsReady);
    }

    [Fact]
    public async Task WaitForAppHostReadyAsync_CompletesWhenStartupStateIsReady()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["REMOTE_APP_HOST_SOCKET_PATH"] = "aspire-remote.sock"
            })
            .Build();

        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<ProfilingTelemetry>()
            .AddSingleton<AppHostStartupState>()
            .BuildServiceProvider();
        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            configuration,
            services.GetRequiredService<ProfilingTelemetry>(),
            services);

        var waitTask = target.WaitForAppHostReadyAsync();
        Assert.False(waitTask.IsCompleted);

        services.GetRequiredService<AppHostStartupState>().MarkReady();
        var ready = await waitTask.DefaultTimeout();

        Assert.True(ready.IsReady);
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_EnumeratesResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddParameter("myparam");
        builder.AddResource(new CustomResource(KnownResourceNames.AspireDashboard));

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0),
            new DcpInstance("myresource-def456", "def456", 1)
        ]));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(resourceWithReplicas.Resource, "myresource-abc123", s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success)
        }).DefaultTimeout();
        await notificationService.PublishUpdateAsync(resourceWithReplicas.Resource, "myresource-def456", s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success)
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        // Dashboard resource should now be included
        Assert.Contains(result, r => r.Name == KnownResourceNames.AspireDashboard);

        // Parameter resource (no replicas) should be returned with matching Name/DisplayName
        var paramSnapshot = Assert.Single(result, r => r.Name == "myparam");
        Assert.Equal("myparam", paramSnapshot.DisplayName);
        Assert.Equal("Parameter", paramSnapshot.ResourceType);

        // Resource with DcpInstancesAnnotation should return multiple instances
        Assert.Contains(result, r => r.Name == "myresource-abc123");
        Assert.Contains(result, r => r.Name == "myresource-def456");
        Assert.All(result.Where(r => r.Name.StartsWith("myresource-")), r => Assert.Equal("myresource", r.DisplayName));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_MapsSnapshotData()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var custom = builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var createdAt = DateTime.UtcNow.AddMinutes(-5);
        var startedAt = DateTime.UtcNow.AddMinutes(-4);

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(custom.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            CreationTimeStamp = createdAt,
            StartTimeStamp = startedAt,
            Urls = [
                new UrlSnapshot("http", "http://localhost:5000", false) { DisplayProperties = new UrlDisplayPropertiesSnapshot("HTTP Endpoint", 1) },
                new UrlSnapshot("https", "https://localhost:5001", true) { DisplayProperties = new UrlDisplayPropertiesSnapshot("HTTPS Endpoint", 2) },
                new UrlSnapshot("inactive", "http://localhost:5002", false) { IsInactive = true }
            ],
            Relationships = [
                new RelationshipSnapshot("dependency1", "Reference"),
                new RelationshipSnapshot("dependency2", "WaitFor")
            ],
            HealthReports = [
                new HealthReportSnapshot("check1", Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, "All good", null),
                new HealthReportSnapshot("check2", Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, "Failed", "Exception occurred")
            ],
            Volumes = [
                new VolumeSnapshot("/host/path", "/container/path", "bind", false),
                new VolumeSnapshot("myvolume", "/data", "volume", true)
            ],
            EnvironmentVariables = [
                new EnvironmentVariableSnapshot("MY_VAR", "my-value", false),
                new EnvironmentVariableSnapshot("ANOTHER_VAR", "another-value", true)
            ],
            Commands = [
                new ResourceCommandSnapshot("start", ResourceCommandState.Enabled, "Start", "Start the resource", null, null, null, null, false)
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            Label = "Selector",
                            Description = "CSS selector to click.",
                            EnableDescriptionMarkdown = true,
                            InputType = InputType.Text,
                            Required = true,
                            Placeholder = "#submit",
                            Options =
                            [
                                new("mode", "Primary"),
                                new("mode", "Secondary")
                            ],
                            Disabled = true,
                            MaxLength = 128,
                            DynamicLoading = new InputLoadOptions
                            {
                                AlwaysLoadOnStart = true,
                                DependsOnInputs = ["browser"],
                                LoadCallback = _ => Task.CompletedTask
                            }
                        }
                    ],
                    Visibility = ResourceCommandVisibility.Api
                },
                new ResourceCommandSnapshot("stop", ResourceCommandState.Disabled, "Stop", "Stop the resource", null, null, null, null, false),
                new ResourceCommandSnapshot("restart", ResourceCommandState.Hidden, "Restart", null, null, null, null, null, true)
            ],
            Properties = [
                new ResourcePropertySnapshot(CustomResourceKnownProperties.Source, "normal-value"),
                new ResourcePropertySnapshot("ConnectionString", "secret-value") { IsSensitive = true }
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result);

        // State
        Assert.Equal("Running", snapshot.State);
        Assert.Equal(KnownResourceStateStyles.Success, snapshot.StateStyle);

        // Timestamps
        Assert.Equal(createdAt, snapshot.CreatedAt);
        Assert.Equal(startedAt, snapshot.StartedAt);

        // URLs (inactive URLs should be excluded)
        Assert.Equal(2, snapshot.Urls.Length);
        Assert.Contains(snapshot.Urls, u => u.Name == "http" && u.Url == "http://localhost:5000" && !u.IsInternal);
        Assert.Contains(snapshot.Urls, u => u.Name == "https" && u.Url == "https://localhost:5001" && u.IsInternal);
        Assert.DoesNotContain(snapshot.Urls, u => u.Name == "inactive");

        // URL display properties
        var httpUrl = snapshot.Urls.Single(u => u.Name == "http");
        Assert.NotNull(httpUrl.DisplayProperties);
        Assert.Equal("HTTP Endpoint", httpUrl.DisplayProperties.DisplayName);
        Assert.Equal(1, httpUrl.DisplayProperties.SortOrder);

        var httpsUrl = snapshot.Urls.Single(u => u.Name == "https");
        Assert.NotNull(httpsUrl.DisplayProperties);
        Assert.Equal("HTTPS Endpoint", httpsUrl.DisplayProperties.DisplayName);
        Assert.Equal(2, httpsUrl.DisplayProperties.SortOrder);

        // Relationships
        Assert.Equal(2, snapshot.Relationships.Length);
        Assert.Contains(snapshot.Relationships, r => r.ResourceName == "dependency1" && r.Type == "Reference");
        Assert.Contains(snapshot.Relationships, r => r.ResourceName == "dependency2" && r.Type == "WaitFor");

        // Health reports
        Assert.Equal(2, snapshot.HealthReports.Length);
        Assert.Contains(snapshot.HealthReports, h => h.Name == "check1" && h.Status == "Healthy");
        Assert.Contains(snapshot.HealthReports, h => h.Name == "check2" && h.Status == "Unhealthy" && h.ExceptionText == "Exception occurred");

        // Volumes
        Assert.Equal(2, snapshot.Volumes.Length);
        Assert.Contains(snapshot.Volumes, v => v.Source == "/host/path" && v.Target == "/container/path" && !v.IsReadOnly);
        Assert.Contains(snapshot.Volumes, v => v.Source == "myvolume" && v.Target == "/data" && v.IsReadOnly);

        // Environment variables
        Assert.Equal(2, snapshot.EnvironmentVariables.Length);
        Assert.Contains(snapshot.EnvironmentVariables, e => e.Name == "MY_VAR" && e.Value == "my-value" && !e.IsFromSpec);
        Assert.Contains(snapshot.EnvironmentVariables, e => e.Name == "ANOTHER_VAR" && e.Value == "another-value" && e.IsFromSpec);

        // Commands
        Assert.Equal(3, snapshot.Commands.Length);
        var startCommand = Assert.Single(snapshot.Commands, c => c.Name == "start" && c.DisplayName == "Start" && c.Description == "Start the resource" && c.State == "Enabled");
        var argumentInput = Assert.Single(startCommand.ArgumentInputs);
        Assert.Equal("selector", argumentInput.Name);
        Assert.Equal("Selector", argumentInput.Label);
        Assert.Equal("CSS selector to click.", argumentInput.Description);
        Assert.True(argumentInput.EnableDescriptionMarkdown);
        Assert.Equal(nameof(InputType.Text), argumentInput.InputType);
        Assert.True(argumentInput.Required);
        Assert.Equal("#submit", argumentInput.Placeholder);
        Assert.Equal("Secondary", argumentInput.Options!["mode"]);
        Assert.True(argumentInput.Disabled);
        Assert.Equal(128, argumentInput.MaxLength);
        Assert.NotNull(argumentInput.DynamicLoading);
        Assert.True(argumentInput.DynamicLoading.AlwaysLoadOnStart);
        Assert.Equal("browser", Assert.Single(argumentInput.DynamicLoading.DependsOnInputs!));
        Assert.Equal(nameof(ResourceCommandVisibility.Api), startCommand.Visibility);
        Assert.Contains(snapshot.Commands, c => c.Name == "stop" && c.DisplayName == "Stop" && c.Description == "Stop the resource" && c.State == "Disabled");
        Assert.Contains(snapshot.Commands, c => c.Name == "restart" && c.DisplayName == "Restart" && c.Description == null && c.State == "Hidden");

        // Properties (sensitive values should be redacted)
        Assert.True(snapshot.Properties.TryGetValue(CustomResourceKnownProperties.Source, out var normalValue));
        var normalJsonValue = Assert.IsAssignableFrom<JsonValue>(normalValue);
        Assert.Equal("normal-value", normalJsonValue.GetValue<string>());
        Assert.True(snapshot.Properties.TryGetValue("ConnectionString", out var sensitiveValue));
        Assert.Null(sensitiveValue);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RedactsSecretParameterValuesInEnvironmentVariables()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddParameter("dbpassword", "s3cr3t-value", secret: true);
        builder.AddParameter("region", "public-value", secret: false);
        var custom = builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(custom.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("DB_PASSWORD", "s3cr3t-value", true),
                new EnvironmentVariableSnapshot("REGION", "public-value", true),
                new EnvironmentVariableSnapshot("PLAIN_VAR", "plain-value", true)
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result, r => r.Name == "myresource");

        // The value that matches the secret parameter must be redacted; other values are untouched.
        var dbPassword = Assert.Single(snapshot.EnvironmentVariables, e => e.Name == "DB_PASSWORD");
        Assert.Null(dbPassword.Value);
        var region = Assert.Single(snapshot.EnvironmentVariables, e => e.Name == "REGION");
        Assert.Equal("public-value", region.Value);
        var plainVar = Assert.Single(snapshot.EnvironmentVariables, e => e.Name == "PLAIN_VAR");
        Assert.Equal("plain-value", plainVar.Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RedactsSecretParameterReferencedByOwningResourceEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // Mimic AddPostgres: a generated secret password parameter that is referenced by the owning
        // resource's own environment but is never registered as a top-level resource in the model. Prior
        // to the fix for https://github.com/microsoft/aspire/issues/19241 the redaction set only contained
        // top-level ParameterResources, so the owning resource leaked this value in plaintext even though
        // the same value was redacted when it flowed into a dependent resource.
        var passwordParameter = new ParameterResource("pg-password", _ => "generated-s3cr3t", secret: true);
        var owner = builder.AddResource(new CustomResourceWithEnvironment("pg"))
            .WithEnvironment(context => context.EnvironmentVariables["POSTGRES_PASSWORD"] = passwordParameter);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        // Simulate the generated parameter having been resolved to its runtime value.
        passwordParameter.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        passwordParameter.WaitForValueTcs.SetResult("generated-s3cr3t");

        // Cache the env callback the way DCP does on start, so peek-only discovery can observe the reference.
        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("POSTGRES_PASSWORD", "generated-s3cr3t", true)
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result, r => r.Name == "pg");
        var password = Assert.Single(snapshot.EnvironmentVariables, e => e.Name == "POSTGRES_PASSWORD");

        // The owning resource's own environment variable must be redacted even though the secret parameter
        // is only referenced by the resource and is not a top-level resource in the model.
        Assert.Null(password.Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RedactsDistinctSecretParametersWithSameName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var topLevelParameter = builder.AddParameter("shared-name", "top-level-secret", secret: true);
        var referencedParameter = new ParameterResource("shared-name", _ => "referenced-secret", secret: true);
        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(context => context.EnvironmentVariables["REFERENCED_SECRET"] = referencedParameter);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        topLevelParameter.Resource.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        topLevelParameter.Resource.WaitForValueTcs.SetResult("top-level-secret");
        referencedParameter.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        referencedParameter.WaitForValueTcs.SetResult("referenced-secret");

        // Cache the env callback the way DCP does on start, so peek-only discovery can observe the reference.
        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("TOP_LEVEL_SECRET", "top-level-secret", true),
                new EnvironmentVariableSnapshot("REFERENCED_SECRET", "referenced-secret", true)
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result, r => r.Name == "owner");
        Assert.Null(Assert.Single(snapshot.EnvironmentVariables, e => e.Name == "TOP_LEVEL_SECRET").Value);
        Assert.Null(Assert.Single(snapshot.EnvironmentVariables, e => e.Name == "REFERENCED_SECRET").Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RedactsNewlyReferencedSecret_AfterRestartRepointsResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // The set of secret parameters a resource references is not fixed for the connection lifetime. DCP
        // clears and re-evaluates a resource's environment/argument callbacks when it restarts (see
        // DcpExecutor.ForgetCachedCallbackResults), so a restart can make a resource reference a secret it
        // did not reference before. Discovery is peek-only (it reads the callback results DCP cached on start
        // and never invokes a callback), so this test primes the cache like DCP does, then simulates a restart
        // between two snapshot calls on the same target — flipping the referenced secret, clearing the cached
        // callback result, and re-priming exactly as a restart does — and asserts the second call redacts the
        // newly referenced secret.
        var secretA = new ParameterResource("secret-a", _ => "value-a", secret: true);
        secretA.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secretA.WaitForValueTcs.SetResult("value-a");

        var secretB = new ParameterResource("secret-b", _ => "value-b", secret: true);
        secretB.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secretB.WaitForValueTcs.SetResult("value-b");

        // The environment callback references whichever secret this local points at when it is evaluated.
        // The callback is registered once, before the host is built, so the resource's annotation collection
        // is never mutated on a live host — mutating annotations after StartAsync races with background
        // annotation evaluation and makes the test flaky. The restart is simulated below by flipping this
        // local, clearing the callback's cached result, and re-priming, not by adding another annotation.
        var referencedSecret = secretA;
        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(context => context.EnvironmentVariables["SECRET"] = referencedSecret);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        // Cache the callback result (referencing secretA) the way DCP does when it first starts the resource.
        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-a", true)
            ]
        }).DefaultTimeout();

        // The same target instance is reused across both calls so the add-only accumulator persists between
        // them, exactly as it would over the lifetime of a single describe/watch connection.
        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var firstResult = await target.GetResourceSnapshotsAsync().DefaultTimeout();
        var firstSnapshot = Assert.Single(firstResult, r => r.Name == "owner");
        Assert.Null(Assert.Single(firstSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        // Simulate a restart: the resource now references a different secret. Clear the previously cached
        // callback result exactly as DcpExecutor.ForgetCachedCallbackResults does, then re-prime so the cache
        // holds the new reference (secretB) as it would after DCP re-evaluates on restart.
        referencedSecret = secretB;
        var environmentCallback = owner.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Single();
        environmentCallback.AsCallbackAnnotation().ForgetCachedResult();
        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-b", true)
            ]
        }).DefaultTimeout();

        var secondResult = await target.GetResourceSnapshotsAsync().DefaultTimeout();
        var secondSnapshot = Assert.Single(secondResult, r => r.Name == "owner");

        // After the restart the redaction set must reflect the newly referenced secret. Peek-only discovery on
        // the second call observes secretB (freshly cached), so value-b is redacted.
        Assert.Null(Assert.Single(secondSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RetainsPreviouslyReferencedSecret_AfterRestartRepointsResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // Regression test for the leak adamint identified in review of the #19241 fix: during a restart, a
        // still-in-flight snapshot from the prior incarnation can carry the OLD secret value while the resource's
        // callback has already been re-pointed at a new secret. If discovery only redacted the current pass's
        // set, the stale old value would be emitted in plaintext. The per-connection redaction set is add-only,
        // so a secret observed on an earlier pass stays redacted even after the resource stops referencing it.
        var secretA = new ParameterResource("secret-a", _ => "value-a", secret: true);
        secretA.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secretA.WaitForValueTcs.SetResult("value-a");

        var secretB = new ParameterResource("secret-b", _ => "value-b", secret: true);
        secretB.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secretB.WaitForValueTcs.SetResult("value-b");

        var referencedSecret = secretA;
        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(context => context.EnvironmentVariables["SECRET"] = referencedSecret);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-a", true)
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        // First pass observes secretA and adds it to the connection's redaction set.
        var firstResult = await target.GetResourceSnapshotsAsync().DefaultTimeout();
        var firstSnapshot = Assert.Single(firstResult, r => r.Name == "owner");
        Assert.Null(Assert.Single(firstSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        // Restart re-points the resource at secretB, but a lagging snapshot from the old incarnation still
        // carries value-a. Re-point and re-prime the cache so discovery would, on its own, only find secretB.
        referencedSecret = secretB;
        var environmentCallback = owner.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Single();
        environmentCallback.AsCallbackAnnotation().ForgetCachedResult();
        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Stopping", KnownResourceStateStyles.Info),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-a", true)
            ]
        }).DefaultTimeout();

        var secondResult = await target.GetResourceSnapshotsAsync().DefaultTimeout();
        var secondSnapshot = Assert.Single(secondResult, r => r.Name == "owner");

        // The stale value-a must still be redacted. Without add-only accumulation, discovery on this pass finds
        // only secretB and would publish value-a in plaintext — the exact leak the reviewer called out.
        Assert.Null(Assert.Single(secondSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RetainsPreviousSecretValue_AfterParameterValueIsReplaced()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // Regression test for the leak the reviewer identified: the runtime "Set parameter" path replaces a
        // parameter's already-completed WaitForValueTcs with a new value (ParameterProcessor.SetParameterValue),
        // so re-resolving the SAME retained parameter object later yields only the new value. An already-published
        // or still-current snapshot can still carry the previous secret value, so redaction accumulates resolved
        // secret STRINGS add-only — retaining the parameter object alone is not enough because its value has been
        // overwritten in place. The owner keeps referencing the same parameter throughout; only its value changes.
        var secret = new ParameterResource("secret", _ => "value-a", secret: true);
        secret.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secret.WaitForValueTcs.SetResult("value-a");

        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(context => context.EnvironmentVariables["SECRET"] = secret);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-a", true)
            ]
        }).DefaultTimeout();

        // The same target instance is reused across both calls so the add-only accumulator persists between them,
        // exactly as it would over the lifetime of a single describe/watch connection.
        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        // First pass observes value-a and adds it to the connection's redaction set.
        var firstResult = await target.GetResourceSnapshotsAsync().DefaultTimeout();
        var firstSnapshot = Assert.Single(firstResult, r => r.Name == "owner");
        Assert.Null(Assert.Single(firstSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        // The runtime replaces the parameter's resolved value with value-b (as SetParameterValue does by swapping
        // the completed WaitForValueTcs), but a lagging snapshot still carries value-a. Re-resolving the same
        // parameter object now yields only value-b.
        secret.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secret.WaitForValueTcs.SetResult("value-b");

        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-a", true)
            ]
        }).DefaultTimeout();

        var secondResult = await target.GetResourceSnapshotsAsync().DefaultTimeout();
        var secondSnapshot = Assert.Single(secondResult, r => r.Name == "owner");

        // value-a must still be redacted. Without add-only value accumulation, re-resolving the parameter yields
        // only value-b, so the stale value-a would be emitted in plaintext — the exact leak the reviewer called out.
        Assert.Null(Assert.Single(secondSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RetainsPreviousSecretValue_AcrossSeparateConnections()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // Regression test for the AppHost-scope gap the reviewer identified: every backchannel connection gets its
        // own AuxiliaryBackchannelRpcTarget (AuxiliaryBackchannelService.HandleClientConnectionAsync). If the
        // redaction history lived on the target, a client that connected AFTER a secret's value was replaced would
        // start with an empty set and leak the previous value carried by a lagging snapshot. The history is
        // AppHost-scoped (the SecretRedactionHistory singleton) and shared by every target, so a value one
        // connection observed stays redacted for a later, independently constructed connection.
        var secret = new ParameterResource("secret", _ => "value-a", secret: true);
        secret.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secret.WaitForValueTcs.SetResult("value-a");

        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(context => context.EnvironmentVariables["SECRET"] = secret);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-a", true)
            ]
        }).DefaultTimeout();

        // First connection observes value-a and records it in the shared, AppHost-scoped history.
        var firstConnection = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var firstResult = await firstConnection.GetResourceSnapshotsAsync().DefaultTimeout();
        var firstSnapshot = Assert.Single(firstResult, r => r.Name == "owner");
        Assert.Null(Assert.Single(firstSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        // The runtime replaces the parameter's resolved value with value-b (as SetParameterValue does by swapping
        // the completed WaitForValueTcs), but a lagging snapshot still carries value-a.
        secret.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secret.WaitForValueTcs.SetResult("value-b");

        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-a", true)
            ]
        }).DefaultTimeout();

        // A second, independently constructed connection that never observed value-a itself must still redact it,
        // because the redaction history is shared across connections for the life of the AppHost.
        var secondConnection = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var secondResult = await secondConnection.GetResourceSnapshotsAsync().DefaultTimeout();
        var secondSnapshot = Assert.Single(secondResult, r => r.Name == "owner");

        // value-a must still be redacted. With per-connection history the fresh connection would start empty and
        // resolve only value-b, so the stale value-a would be emitted in plaintext.
        Assert.Null(Assert.Single(secondSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RedactsSecretResolvedDuringAnotherResourceMcpDiscoveryWindow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // Regression test for the per-snapshot concern the reviewer identified: building a resource's snapshot can
        // await MCP tool discovery for up to s_mcpDiscoveryTimeout, and a parameter can resolve during that window.
        // If the redaction set is computed once for the whole describe batch (before the resource loop), a value
        // resolved mid-loop is missing from it, so a LATER resource whose snapshot already carries that value leaks
        // it in plaintext. Resolving the redaction set per snapshot (after the MCP await) closes the window.
        var secret = new ParameterResource("mcp-window-secret", _ => "unused", secret: true);

        // The first resource exposes an MCP endpoint. Its resolver is awaited while its snapshot is built, and here
        // it deterministically resolves the secret — standing in for a parameter that happens to resolve during the
        // real (up to 5s) MCP discovery window. Returning null skips the network TryListToolsAsync call.
        var mcpResource = builder.AddResource(new CustomResourceWithEndpoints("mcp"))
            .WithAnnotation(new McpServerEndpointAnnotation((resource, cancellationToken) =>
            {
                secret.WaitForValueTcs!.TrySetResult("leaked-during-mcp");
                return Task.FromResult<Uri?>(null);
            }));

        // The second resource owns an environment variable carrying the secret's resolved value. It is registered
        // AFTER the MCP resource so its snapshot is built after the MCP resolver has run.
        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(context => context.EnvironmentVariables["SECRET"] = secret);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        // Start the secret UNRESOLVED; the MCP resolver completes it mid-loop while the "owner" snapshot is still to
        // be built. (A fresh uncompleted TCS also discards any startup resolution of the referenced parameter.)
        secret.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(mcpResource.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success)
        }).DefaultTimeout();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "leaked-during-mcp", true)
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var ownerSnapshot = Assert.Single(result, r => r.Name == "owner");

        // The owner's snapshot is built after the MCP resource resolved the secret. A per-snapshot redaction set
        // observes the now-resolved value and redacts it; a batch-wide set computed before the loop would not.
        Assert.Null(Assert.Single(ownerSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RedactsSecretReplacedBeforeFirstConnection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // Regression test for the cold-start residual the reviewer identified: a backchannel connection only observes
        // a secret once it is open. A value assigned at startup and then replaced before the FIRST connection would,
        // with connection-time observation alone, be absent from the redaction history — a fresh connection peeking
        // the current value would find only the replacement and leak the original from a lagging snapshot. The
        // parameter processor records secret values at assignment time (see its SecretRedactionHistory wiring), so
        // the original value is in the history from startup, independent of any connection.
        var coldSecret = builder.AddParameter("cold-secret", "value-a", secret: true);
        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(context => context.EnvironmentVariables["SECRET"] = coldSecret.Resource);

        using var app = builder.Build();

        // Startup resolves the parameter to value-a; assignment-time recording adds value-a to the AppHost-scoped
        // redaction history before any backchannel connection exists.
        await app.StartAsync().DefaultTimeout();

        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        // The value is replaced with value-b in place (as the runtime "Set parameter" path does). Peek-only discovery
        // on a first-ever connection would now resolve only value-b, so value-a can only stay redacted via the
        // assignment-time record captured at startup.
        coldSecret.Resource.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        coldSecret.Resource.WaitForValueTcs.SetResult("value-b");

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("SECRET", "value-a", true)
            ]
        }).DefaultTimeout();

        // A brand-new connection that never observed value-a must still redact it.
        var firstConnection = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await firstConnection.GetResourceSnapshotsAsync().DefaultTimeout();
        var ownerSnapshot = Assert.Single(result, r => r.Name == "owner");

        Assert.Null(Assert.Single(ownerSnapshot.EnvironmentVariables, e => e.Name == "SECRET").Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_DoesNotInvokeUncachedResourceCallback_AndLeavesItEvaluable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // Peek-only discovery must never invoke a resource callback: it reads only results DCP already cached.
        // A callback that has not been cached yet is simply skipped, so describe succeeds without running it, and
        // the callback is left untouched — no cached result, still evaluable exactly as authored. This replaces
        // the previous "fail closed when the callback throws during discovery" test: discovery no longer invokes
        // callbacks, so a throwing callback can never be reached (and thus never poisoned) by describe.
        var invocations = 0;
        Action<EnvironmentCallbackContext> throwingCallback = _ =>
        {
            invocations++;
            throw new InvalidOperationException("resource callbacks must not be invoked by describe discovery");
        };
        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(throwingCallback);

        using var app = builder.Build();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        // Describe succeeds because the uncached, throwing callback is peeked (found absent) rather than invoked.
        _ = await target.GetResourceSnapshotsAsync().DefaultTimeout();
        Assert.Equal(0, invocations);

        // Discovery left the cache pristine: nothing was cached, so DCP can still evaluate the callback and it
        // behaves exactly as authored (invoked once, throwing its own exception).
        var annotation = owner.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Single();
        Assert.False(annotation.AsCallbackAnnotation().TryGetCachedResult(out var cached));
        Assert.Null(cached);

        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await annotation.AsCallbackAnnotation()
                .EvaluateOnceAsync(new EnvironmentCallbackContext(executionContext, owner.Resource, new Dictionary<string, object>()))
                .DefaultTimeout());
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_DoesNotInvokeOrPoisonCallbackCache_WhenDescribeIsCanceled()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // The precise leak adamint identified in review: previously discovery invoked resource callbacks, so a
        // describe/watch cancelled while an unresolved parameter callback awaited the client's cancellation token
        // cached a *cancelled* task that DCP then reused, failing the resource until the next restart. Peek-only
        // discovery never invokes a callback, so even a cancelled describe cannot run it nor cache a cancelled
        // task, and the annotation stays fully evaluable for DCP afterward.
        var invocations = 0;
        Action<EnvironmentCallbackContext> cancelAwareCallback = context =>
        {
            invocations++;
            // If discovery had invoked this under the cancelled describe token (old behaviour), the throw would
            // have been cached as a cancelled/faulted task and reused by DCP.
            context.CancellationToken.ThrowIfCancellationRequested();
            context.EnvironmentVariables["SECRET"] = "resolved";
        };
        var owner = builder.AddResource(new CustomResourceWithEnvironment("owner"))
            .WithEnvironment(cancelAwareCallback);

        using var app = builder.Build();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await target.GetResourceSnapshotsAsync(cts.Token).DefaultTimeout();
        }
        catch (OperationCanceledException)
        {
            // Cancellation surfacing is acceptable; the guarantee under test is that the callback was neither
            // invoked nor cached, regardless of whether the cancelled describe returns or throws.
        }

        var annotation = owner.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Single();
        Assert.Equal(0, invocations);
        Assert.False(annotation.AsCallbackAnnotation().TryGetCachedResult(out var cached));
        Assert.Null(cached);

        // The annotation is still evaluable: DCP evaluates it to a successful, cached result. The earlier
        // cancelled describe left nothing poisoned behind, so the resource is not stuck failing until a restart.
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var evaluated = await annotation.AsCallbackAnnotation()
            .EvaluateOnceAsync(new EnvironmentCallbackContext(executionContext, owner.Resource, new Dictionary<string, object>()))
            .DefaultTimeout();
        Assert.Equal(1, invocations);
        Assert.True(annotation.AsCallbackAnnotation().TryGetCachedResult(out var cachedAfter));
        Assert.True(cachedAfter!.IsCompletedSuccessfully);
        Assert.Equal("resolved", evaluated["SECRET"]);
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_DoesNotBlockOnUnresolvedSecretParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // A secret parameter that is only referenced by a resource (never registered as a top-level parameter) so it
        // is not resolved — and therefore not recorded for redaction — during run-mode startup. This lets the test
        // exercise a genuinely unresolved secret at describe time.
        var secret = new ParameterResource("dbpassword", _ => "s3cr3t-value", secret: true);
        var owner = builder.AddResource(new CustomResourceWithEnvironment("myresource"))
            .WithEnvironment(context => context.EnvironmentVariables["DB_PASSWORD"] = secret);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        // Leave the secret unresolved: its completion source never completes. GetResolvedSecretParameterValues must
        // peek (not await) the task, so an unresolved secret cannot block the snapshot call.
        secret.WaitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cache the env callback the way DCP does on start, so peek-only discovery can observe (and then skip) the
        // unresolved secret reference.
        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(owner.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("DB_PASSWORD", "s3cr3t-value", true)
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        // The call must complete promptly even though a secret parameter is unresolved. DefaultTimeout
        // fails the test if GetResolvedSecretParameterValues ever blocks waiting for resolution.
        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result, r => r.Name == "myresource");
        var dbPassword = Assert.Single(snapshot.EnvironmentVariables, e => e.Name == "DB_PASSWORD");

        // Because the secret value was never resolved, it is not part of the redaction set: the unresolved
        // secret is skipped rather than awaited. Once resolved it is redacted (see the streaming test).
        Assert.Equal("s3cr3t-value", dbPassword.Value);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WatchResourceSnapshotsAsync_RedactsSecretResolvedAfterWatchStarted()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        // A secret parameter referenced by the resource (not a top-level parameter), so it is not resolved or
        // recorded for redaction during run-mode startup and the watch can begin with it genuinely unresolved.
        var secret = new ParameterResource("dbpassword", _ => "s3cr3t-value", secret: true);
        var owner = builder.AddResource(new CustomResourceWithEnvironment("myresource"))
            .WithEnvironment(context => context.EnvironmentVariables["DB_PASSWORD"] = secret);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        // Begin with the secret unresolved so the watch starts before the value is known.
        var waitForValueTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        secret.WaitForValueTcs = waitForValueTcs;

        // Cache the env callback the way DCP does on start, so peek-only discovery can observe the secret reference.
        await PrimeEnvironmentCallbackCacheAsync(owner.Resource, app.Services).DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        using var cts = new CancellationTokenSource();
        var enumerator = target.WatchResourceSnapshotsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            // Phase 1: secret unresolved. The env var value is not redacted because the secret value is unknown.
            await notificationService.PublishUpdateAsync(owner.Resource, s => s with
            {
                State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
                EnvironmentVariables =
                [
                    new EnvironmentVariableSnapshot("PHASE", "before", false),
                    new EnvironmentVariableSnapshot("DB_PASSWORD", "s3cr3t-value", true)
                ]
            }).DefaultTimeout();

            var before = await ReadSnapshotAsync(enumerator, s => s.Name == "myresource" && HasEnvironmentVariable(s, "PHASE", "before")).DefaultTimeout();
            Assert.Equal("s3cr3t-value", GetEnvironmentVariableValue(before, "DB_PASSWORD"));

            // Resolve the secret mid-stream, then push a new event whose env var now matches the secret value.
            waitForValueTcs.SetResult("s3cr3t-value");

            await notificationService.PublishUpdateAsync(owner.Resource, s => s with
            {
                State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
                EnvironmentVariables =
                [
                    new EnvironmentVariableSnapshot("PHASE", "after", false),
                    new EnvironmentVariableSnapshot("DB_PASSWORD", "s3cr3t-value", true)
                ]
            }).DefaultTimeout();

            var after = await ReadSnapshotAsync(enumerator, s => s.Name == "myresource" && HasEnvironmentVariable(s, "PHASE", "after")).DefaultTimeout();

            // The streaming path recomputes the secret set per event, so a secret resolved after the watch
            // started is still redacted on later events and does not bypass the filter.
            Assert.Null(GetEnvironmentVariableValue(after, "DB_PASSWORD"));
        }
        finally
        {
            cts.Cancel();
            await enumerator.DisposeAsync();
        }

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_RedactsNonSecretEnvVarThatCoincidentallyMatchesSecretValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddParameter("dbpassword", "shared-value", secret: true);
        var custom = builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(custom.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            EnvironmentVariables =
            [
                new EnvironmentVariableSnapshot("COINCIDENCE", "shared-value", true)
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result, r => r.Name == "myresource");
        var coincidence = Assert.Single(snapshot.EnvironmentVariables, e => e.Name == "COINCIDENCE");

        // Redaction is value-based: any value equal to a secret value is redacted, even when the env var is
        // not itself sourced from the secret parameter. This is the documented, expected behavior.
        Assert.Null(coincidence.Value);

        await app.StopAsync().DefaultTimeout();
    }

    private static async Task<ResourceSnapshot> ReadSnapshotAsync(IAsyncEnumerator<ResourceSnapshot> enumerator, Func<ResourceSnapshot, bool> predicate)
    {
        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            if (predicate(enumerator.Current))
            {
                return enumerator.Current;
            }
        }

        throw new InvalidOperationException("Watch stream ended before a matching snapshot was observed.");
    }

    private static bool HasEnvironmentVariable(ResourceSnapshot snapshot, string name, string value)
        => snapshot.EnvironmentVariables.Any(e => e.Name == name && e.Value == value);

    private static string? GetEnvironmentVariableValue(ResourceSnapshot snapshot, string name)
        => snapshot.EnvironmentVariables.Single(e => e.Name == name).Value;

    // Mirror what DCP does when it starts a resource: evaluate the resource's environment callbacks once so the
    // result is cached. Peek-only secret discovery (used by describe/watch) never invokes callbacks itself — it
    // only reads results DCP already cached — so a test must prime that cache the same way a real run would before
    // a secret referenced through a callback can be discovered and redacted.
    private static async Task PrimeEnvironmentCallbackCacheAsync(IResource resource, IServiceProvider services)
    {
        var executionContext = services.GetRequiredService<DistributedApplicationExecutionContext>();
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.AsCallbackAnnotation()
                .EvaluateOnceAsync(new EnvironmentCallbackContext(executionContext, resource, new Dictionary<string, object>()))
                .ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WaitForResourceAsync_ReturnsFailureWhenResourceHasErrorStateStyle()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var custom = builder.AddResource(new CustomResource("storage"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(custom.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Failed to Provision Roles", KnownResourceStateStyles.Error)
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.WaitForResourceAsync(new WaitForResourceRequest
        {
            ResourceName = custom.Resource.Name,
            Status = "up",
            TimeoutSeconds = 30
        }).DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(result.TimedOut);
        Assert.Equal("Failed to Provision Roles", result.State);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_MapsNonStringPropertiesAsStringsForLegacyCallers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var custom = builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(custom.Resource, s => s with
        {
            Properties =
            [
                new ResourcePropertySnapshot("number", 42),
                new ResourcePropertySnapshot("flag", true),
                new ResourcePropertySnapshot("list", new[] { "one", "two" }),
                new ResourcePropertySnapshot("ConnectionString", "secret-value") { IsSensitive = true }
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result);
        Assert.Equal("42", Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["number"]).GetValue<string>());
        Assert.Equal(bool.TrueString, Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["flag"]).GetValue<string>());
        Assert.Equal("one,two", Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["list"]).GetValue<string>());
        Assert.Null(snapshot.Properties["ConnectionString"]);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task GetResourcesAsync_MapsNonStringPropertiesAsJsonForV3Callers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var custom = builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(custom.Resource, s => s with
        {
            Properties =
            [
                new ResourcePropertySnapshot("number", 42),
                new ResourcePropertySnapshot("flag", true),
                new ResourcePropertySnapshot("list", new[] { "one", "two" }),
                new ResourcePropertySnapshot("object", new KeyValuePair<string, string>[]
                {
                    new("Host", "localhost"),
                    new("DatabaseName", "catalogdb")
                }),
                new ResourcePropertySnapshot("ConnectionString", "secret-value") { IsSensitive = true }
            ]
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.GetResourcesAsync(new GetResourcesRequest
        {
            ClientCapabilities = [AuxiliaryBackchannelCapabilities.V3]
        }).DefaultTimeout();

        var snapshot = Assert.Single(response.Resources);
        Assert.Equal(42, Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["number"]).GetValue<int>());
        Assert.True(Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["flag"]).GetValue<bool>());
        var list = Assert.IsAssignableFrom<JsonArray>(snapshot.Properties["list"]);
        Assert.Collection(
            list,
            value => Assert.Equal("one", value?.GetValue<string>()),
            value => Assert.Equal("two", value?.GetValue<string>()));
        var nameValueObject = Assert.IsAssignableFrom<JsonObject>(snapshot.Properties["object"]);
        Assert.Equal("localhost", nameValueObject["Host"]?.GetValue<string>());
        Assert.Equal("catalogdb", nameValueObject["DatabaseName"]?.GetValue<string>());
        Assert.Null(snapshot.Properties["ConnectionString"]);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_StampsTerminalPropertiesForTerminalEnabledResource()
    {
        // The dashboard gRPC path stamps terminal.* onto resource snapshots, but `aspire describe`
        // and the VS Code extension read this backchannel path instead. Guard that the backchannel
        // also surfaces terminal availability (and redacts the sensitive consumer UDS path) so the
        // extension's "Open terminal" affordance lights up.
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var custom = builder.AddResource(new CustomResource("myapp"));
        AddSyntheticTerminalAnnotation(custom.Resource, replicaCount: 1);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(custom.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", null)
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result);
        Assert.Equal("true", Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["terminal.enabled"]).GetValue<string>());
        Assert.Equal("0", Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["terminal.replicaIndex"]).GetValue<string>());
        Assert.Equal("1", Assert.IsAssignableFrom<JsonValue>(snapshot.Properties["terminal.replicaCount"]).GetValue<string>());
        // The consumer UDS path is sensitive; it must be present as a key but redacted to null so
        // the host-local socket path never leaks to CLI/extension callers.
        Assert.True(snapshot.Properties.ContainsKey("terminal.consumerUdsPath"));
        Assert.Null(snapshot.Properties["terminal.consumerUdsPath"]);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task GetResourceSnapshotsAsync_DoesNotStampTerminalPropertiesForNonTerminalResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var custom = builder.AddResource(new CustomResource("myapp"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(custom.Resource, s => s with
        {
            State = new ResourceStateSnapshot("Running", null)
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetResourceSnapshotsAsync().DefaultTimeout();

        var snapshot = Assert.Single(result);
        Assert.DoesNotContain(snapshot.Properties.Keys, k => k.StartsWith("terminal.", StringComparison.Ordinal));

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task WaitForResourceAsync_AcceptsResourceId()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0)
        ]));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        var waitTask = target.WaitForResourceAsync(new WaitForResourceRequest
        {
            ResourceName = "myresource-abc123",
            Status = "up",
            TimeoutSeconds = 5
        });

        await notificationService.PublishUpdateAsync(resourceWithReplicas.Resource, "myresource-abc123", s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success)
        }).DefaultTimeout();

        var response = await waitTask.DefaultTimeout();

        Assert.True(response.Success);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WaitForResourceAsync_ResolvesLogicalResourceNameViaAppModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0)
        ]));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        var waitTask = target.WaitForResourceAsync(new WaitForResourceRequest
        {
            ResourceName = "myresource",
            Status = "up",
            TimeoutSeconds = 5
        });

        await notificationService.PublishUpdateAsync(resourceWithReplicas.Resource, "myresource-abc123", s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success)
        }).DefaultTimeout();

        var response = await waitTask.DefaultTimeout();

        Assert.True(response.Success);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WaitForResourceAsync_CancelledSingleInstanceResolvedName_UsesLogicalDisplayName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0)
        ]));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => target.WaitForResourceAsync(new WaitForResourceRequest
        {
            ResourceName = "myresource-abc123",
            Status = "healthy",
            TimeoutSeconds = 5
        }, cancellationTokenSource.Token));

        Assert.Equal("Resource 'myresource' failed to become healthy before the operation was cancelled.", exception.Message);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WaitForResourceAsync_CancelledReplicaResolvedName_UsesReplicaDisplayName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0),
            new DcpInstance("myresource-def456", "def456", 1)
        ]));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => target.WaitForResourceAsync(new WaitForResourceRequest
        {
            ResourceName = "myresource-abc123",
            Status = "healthy",
            TimeoutSeconds = 5
        }, cancellationTokenSource.Token));

        Assert.Equal("Resource 'myresource-abc123' failed to become healthy before the operation was cancelled.", exception.Message);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WaitForResourceAsync_ReturnsAmbiguousErrorForReplicatedLogicalName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0),
            new DcpInstance("myresource-def456", "def456", 1)
        ]));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.WaitForResourceAsync(new WaitForResourceRequest
        {
            ResourceName = "myresource",
            Status = "up",
            TimeoutSeconds = 5
        }).DefaultTimeout();

        Assert.False(response.Success);
        Assert.False(response.ResourceNotFound);
        Assert.Equal("Resource 'myresource' is ambiguous because it has multiple replicas. Specify the exact instance name.", response.ErrorMessage);

        await app.StopAsync().DefaultTimeout();
    }

    private sealed class CustomResource(string name) : Resource(name)
    {
    }

    private sealed class CustomResourceWithEnvironment(string name) : Resource(name), IResourceWithEnvironment
    {
    }

    private sealed class CustomResourceWithEndpoints(string name) : Resource(name), IResourceWithEndpoints
    {
    }

    // Synthesise per-replica terminal layouts directly rather than going through the public
    // WithTerminal() path so the test stays focused on backchannel snapshot stamping and doesn't
    // depend on real DCP terminal-host provisioning. Mirrors DashboardServiceDataTerminalTests.
    private static void AddSyntheticTerminalAnnotation(Resource resource, int replicaCount)
    {
        var hosts = new TerminalHostResource[replicaCount];
        var baseDir = Directory.CreateTempSubdirectory("abrt-").FullName;
        for (var i = 0; i < replicaCount; i++)
        {
            var pseudoId = $"test{i.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(7, '0')}";
            var layout = new TerminalHostLayout(
                replicaId: pseudoId,
                parentReplicaIndex: i,
                producerUdsPath: Path.Combine(baseDir, $"{pseudoId}.dcp.sock"),
                consumerUdsPath: Path.Combine(baseDir, $"{pseudoId}.host.sock"),
                controlUdsPath: Path.Combine(baseDir, $"{pseudoId}.ctrl.sock"),
                metadataPath: Path.Combine(baseDir, $"{pseudoId}.metadata.json"));
            hosts[i] = new TerminalHostResource($"{resource.Name}-terminalhost-{i}", resource, layout);
        }

        var annotation = new TerminalAnnotation(new TerminalOptions { Columns = 132, Rows = 40 });
        annotation.Initialize(hosts);
        resource.Annotations.Add(annotation);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2000, 12, 29, 20, 59, 59, TimeSpan.Zero);
    }

    private const string TestTimestamp = "2000-12-29T20:59:59.0000000Z";

    [Fact]
    public async Task GetResourceLogsAsync_ReturnsLogs_ForSingleResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddResource(new CustomResource("myresource"));
        builder.AddResource(new CustomResource(KnownResourceNames.AspireDashboard));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var logger = resourceLoggerService.GetLogger("myresource");
        logger.LogInformation("Hello from myresource");
        resourceLoggerService.Complete("myresource");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetResourceLogsAsync("myresource", follow: false))
        {
            logs.Add(logLine);
        }

        var log = Assert.Single(logs);
        Assert.Equal("myresource", log.ResourceName);
        Assert.Equal($"{TestTimestamp} Hello from myresource", log.Content);
        Assert.Equal(0, log.LineNumber);
        Assert.False(log.IsError);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceLogsAsync_ReturnsEmpty_WhenResourceNotFound()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetResourceLogsAsync("nonexistent", follow: false))
        {
            logs.Add(logLine);
        }

        Assert.Empty(logs);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceLogsAsync_ReturnsLogsFromAllResources_WhenNoResourceNameSpecified()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddResource(new CustomResource("resource1"));
        builder.AddResource(new CustomResource("resource2"));
        builder.AddResource(new CustomResource(KnownResourceNames.AspireDashboard));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var logger1 = resourceLoggerService.GetLogger("resource1");
        logger1.LogInformation("Log from resource1");
        resourceLoggerService.Complete("resource1");

        var logger2 = resourceLoggerService.GetLogger("resource2");
        logger2.LogInformation("Log from resource2");
        resourceLoggerService.Complete("resource2");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetResourceLogsAsync(resourceName: null, follow: false))
        {
            logs.Add(logLine);
        }

        Assert.Equal(2, logs.Count);

        var log1 = Assert.Single(logs, l => l.ResourceName == "resource1");
        Assert.Equal($"{TestTimestamp} Log from resource1", log1.Content);

        var log2 = Assert.Single(logs, l => l.ResourceName == "resource2");
        Assert.Equal($"{TestTimestamp} Log from resource2", log2.Content);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetConsoleLogsAsync_AppliesSearchAndTailForSingleResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var logger = resourceLoggerService.GetLogger("myresource");
        logger.LogInformation("needle first");
        logger.LogInformation("haystack");
        logger.LogInformation("needle second");
        logger.LogInformation("needle third");
        resourceLoggerService.Complete("myresource");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetConsoleLogsAsync(new GetConsoleLogsRequest
        {
            ResourceName = "myresource",
            Search = "needle",
            Tail = 2,
            IncludeHidden = true
        }))
        {
            logs.Add(logLine);
        }

        Assert.Collection(logs,
            log => Assert.Equal($"{TestTimestamp} needle second", log.Content),
            log => Assert.Equal($"{TestTimestamp} needle third", log.Content));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetConsoleLogsAsync_AppliesSearchAfterStrippingAnsiControlSequences()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var logger = resourceLoggerService.GetLogger("myresource");
        logger.LogInformation("Re\u001b[31mady");
        logger.LogInformation("haystack");
        resourceLoggerService.Complete("myresource");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetConsoleLogsAsync(new GetConsoleLogsRequest
        {
            ResourceName = "myresource",
            Search = "Ready",
            IncludeHidden = true
        }))
        {
            logs.Add(logLine);
        }

        var log = Assert.Single(logs);
        Assert.Equal($"{TestTimestamp} Re\u001b[31mady", log.Content);

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task GetConsoleLogBatchesAsync_AppliesSearchAndTailForSingleResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var logger = resourceLoggerService.GetLogger("myresource");
        logger.LogInformation("needle first");
        logger.LogInformation("haystack");
        logger.LogInformation("needle second");
        logger.LogInformation("needle third");
        resourceLoggerService.Complete("myresource");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var batch in target.GetConsoleLogBatchesAsync(new GetConsoleLogsRequest
        {
            ResourceName = "myresource",
            Search = "needle",
            Tail = 2,
            IncludeHidden = true
        }))
        {
            logs.AddRange(batch.Lines);
        }

        Assert.Collection(logs,
            log => Assert.Equal($"{TestTimestamp} needle second", log.Content),
            log => Assert.Equal($"{TestTimestamp} needle third", log.Content));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetConsoleLogsAsync_DoesNotApplyTailAcrossMultipleResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddResource(new CustomResource("resource1"));
        builder.AddResource(new CustomResource("resource2"));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var logger1 = resourceLoggerService.GetLogger("resource1");
        logger1.LogInformation("resource1 log");
        resourceLoggerService.Complete("resource1");

        var logger2 = resourceLoggerService.GetLogger("resource2");
        logger2.LogInformation("resource2 log");
        resourceLoggerService.Complete("resource2");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetConsoleLogsAsync(new GetConsoleLogsRequest
        {
            Tail = 1,
            IncludeHidden = true
        }))
        {
            logs.Add(logLine);
        }

        Assert.Equal(2, logs.Count);
        Assert.Contains(logs, log => log.ResourceName == "resource1");
        Assert.Contains(logs, log => log.ResourceName == "resource2");

        await app.StopAsync().DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task GetConsoleLogsAsync_ExcludesHiddenResourcesWhenRequested()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddResource(new CustomResource("visible"));
        var hidden = builder.AddResource(new CustomResource("hidden"));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(hidden.Resource, snapshot => snapshot with
        {
            IsHidden = true
        }).DefaultTimeout();

        var visibleLogger = resourceLoggerService.GetLogger("visible");
        visibleLogger.LogInformation("needle visible");
        resourceLoggerService.Complete("visible");

        var hiddenLogger = resourceLoggerService.GetLogger("hidden");
        hiddenLogger.LogInformation("needle hidden");
        resourceLoggerService.Complete("hidden");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetConsoleLogsAsync(new GetConsoleLogsRequest
        {
            Search = "needle",
            IncludeHidden = false
        }))
        {
            logs.Add(logLine);
        }

        var log = Assert.Single(logs);
        Assert.Equal("visible", log.ResourceName);
        Assert.Equal($"{TestTimestamp} needle visible", log.Content);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceLogsAsync_ReturnsLogsFromReplicas()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0),
            new DcpInstance("myresource-def456", "def456", 1)
        ]));

        var otherResource = builder.AddResource(new CustomResource("otherresource"));
        otherResource.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("otherresource-xyz789", "xyz789", 0)
        ]));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var logger1 = resourceLoggerService.GetLogger("myresource-abc123");
        logger1.LogInformation("Log from replica 1");
        resourceLoggerService.Complete("myresource-abc123");

        var logger2 = resourceLoggerService.GetLogger("myresource-def456");
        logger2.LogInformation("Log from replica 2");
        resourceLoggerService.Complete("myresource-def456");

        var otherLogger = resourceLoggerService.GetLogger("otherresource-xyz789");
        otherLogger.LogInformation("Log from other resource");
        resourceLoggerService.Complete("otherresource-xyz789");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetResourceLogsAsync("myresource", follow: false))
        {
            logs.Add(logLine);
        }

        Assert.Equal(2, logs.Count);

        var replica1 = Assert.Single(logs, l => l.ResourceName == "myresource-abc123");
        Assert.Equal($"{TestTimestamp} Log from replica 1", replica1.Content);

        var replica2 = Assert.Single(logs, l => l.ResourceName == "myresource-def456");
        Assert.Equal($"{TestTimestamp} Log from replica 2", replica2.Content);

        Assert.DoesNotContain(logs, l => l.ResourceName == "otherresource-xyz789");

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceLogsAsync_ReturnsLogsForSingleReplica_WhenResolvedInstanceNameIsPassed()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0),
            new DcpInstance("myresource-def456", "def456", 1)
        ]));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var logger1 = resourceLoggerService.GetLogger("myresource-abc123");
        logger1.LogInformation("Log from replica 1");
        resourceLoggerService.Complete("myresource-abc123");

        var logger2 = resourceLoggerService.GetLogger("myresource-def456");
        logger2.LogInformation("Log from replica 2");
        resourceLoggerService.Complete("myresource-def456");

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var logs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetResourceLogsAsync("myresource-def456", follow: false))
        {
            logs.Add(logLine);
        }

        var log = Assert.Single(logs);
        Assert.Equal("myresource-def456", log.ResourceName);
        Assert.Equal($"{TestTimestamp} Log from replica 2", log.Content);

        var badLogs = new List<ResourceLogLine>();
        await foreach (var logLine in target.GetResourceLogsAsync("myresource-nonexistent", follow: false))
        {
            badLogs.Add(logLine);
        }

        Assert.Empty(badLogs);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetResourceLogsAsync_FollowMode_StreamsLogs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();

        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        resourceLoggerService.TimeProvider = new FixedTimeProvider();

        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        using var cts = new CancellationTokenSource();
        var logs = new List<ResourceLogLine>();

        var collectTask = Task.Run(async () =>
        {
            await foreach (var logLine in target.GetResourceLogsAsync("myresource", follow: true, cts.Token))
            {
                logs.Add(logLine);
                if (logs.Count >= 2)
                {
                    break;
                }
            }
        });

        // Write logs after starting the watch
        var logger = resourceLoggerService.GetLogger("myresource");
        logger.LogInformation("First log");
        logger.LogInformation("Second log");

        await collectTask.DefaultTimeout();

        Assert.Equal(2, logs.Count);

        Assert.Equal("myresource", logs[0].ResourceName);
        Assert.Equal($"{TestTimestamp} First log", logs[0].Content);

        Assert.Equal("myresource", logs[1].ResourceName);
        Assert.Equal($"{TestTimestamp} Second log", logs[1].Content);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetDashboardUrlsAsync_ReturnsBaseUrl_WhenDashboardAllowsAnonymousAccess()
    {
        var activities = new List<Activity>();
        using var listener = ActivityListenerHelper.Create(ProfilingTelemetry.ActivitySource, onActivityStopped: activities.Add);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = false,
            outputHelper,
            $"{KnownAspNetCoreConfigNames.Urls}=http://localhost",
            $"{KnownConfigNames.DashboardOtlpGrpcEndpointUrl}=http://localhost",
            $"{KnownConfigNames.DashboardUnsecuredAllowAnonymous}=true",
            $"{KnownConfigNames.ProfilingEnabled}=true");

        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();
        activities.Clear();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var dashboard = Assert.Single(model.Resources, r => r.Name == KnownResourceNames.AspireDashboard);
        var endpoint = dashboard.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == "http");
        endpoint.AllocatedEndpoint = new(endpoint, "localhost", 18888, targetPortExpression: "18888");

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(dashboard, snapshot => snapshot with
        {
            State = KnownResourceStates.Running,
            ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
        }).DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetDashboardUrlsAsync().DefaultTimeout();

        Assert.True(result.DashboardHealthy);
        Assert.Equal("http://localhost:18888", result.BaseUrlWithLoginToken);
        Assert.Null(result.CodespacesUrlWithLoginToken);

        var dashboardActivityNames = activities.Select(activity => activity.OperationName).ToArray();
        Assert.Contains(ProfilingTelemetry.Activities.JsonRpcServerCall, dashboardActivityNames);
        Assert.Contains(ProfilingTelemetry.Activities.DashboardGetConnectionInfo, dashboardActivityNames);
        Assert.Contains(ProfilingTelemetry.Activities.DashboardWaitHealthy, dashboardActivityNames);
        Assert.Contains(ProfilingTelemetry.Activities.DashboardResolveUrls, dashboardActivityNames);

        var resolveActivity = Assert.Single(activities, activity => activity.OperationName == ProfilingTelemetry.Activities.DashboardResolveUrls);
        Assert.Equal(ProfilingTelemetry.Values.DashboardUrlSourceResource, resolveActivity.GetTagItem(ProfilingTelemetry.Tags.DashboardUrlSource));
        Assert.Equal(true, resolveActivity.GetTagItem(ProfilingTelemetry.Tags.DashboardHasApiBaseUrl));

        var connectionInfoActivity = Assert.Single(activities, activity => activity.OperationName == ProfilingTelemetry.Activities.DashboardGetConnectionInfo);
        Assert.Equal(true, connectionInfoActivity.GetTagItem(ProfilingTelemetry.Tags.DashboardHealthy));
        Assert.Equal(ProfilingTelemetry.Values.DashboardUrlSourceResource, connectionInfoActivity.GetTagItem(ProfilingTelemetry.Tags.DashboardUrlSource));
    }

    [Fact]
    public void JsonRpcServerProfilingSpan_UsesJsonRpcRemoteParent()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ProfilingTelemetry.ActivitySourceName or "test.client" or "test.jsonrpc",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == ProfilingTelemetry.ActivitySourceName)
                {
                    activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.ProfilingEnabled] = "true",
                [KnownConfigNames.ProfilingSessionId] = "session-1"
            })
            .Build();
        var profilingTelemetry = new ProfilingTelemetry(configuration);
        using var clientSource = new ActivitySource("test.client");
        using var jsonRpcSource = new ActivitySource("test.jsonrpc");
        var clientActivity = clientSource.StartActivity("client", ActivityKind.Client);
        Assert.NotNull(clientActivity);
        var clientContext = clientActivity.Context;
        var clientSpanId = clientActivity.SpanId;
        clientActivity.Dispose();

        using (var jsonRpcActivity = jsonRpcSource.StartActivity("server", ActivityKind.Server, clientContext))
        {
            Assert.NotNull(jsonRpcActivity);
            using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(AuxiliaryBackchannelRpcTarget.GetDashboardUrlsAsync), streaming: false);
        }

        var serverActivity = Assert.Single(activities, activity => activity.OperationName == ProfilingTelemetry.Activities.JsonRpcServerCall);
        Assert.Equal(clientSpanId, serverActivity.ParentSpanId);
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_MapsJsonArgumentsToInteractionInputs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myresource"));
        custom.WithCommand(
            name: "click",
            displayName: "Click",
            executeCommand: context =>
            {
                capturedArguments = context.Arguments;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "selector",
                        InputType = InputType.Text
                    },
                    new InteractionInput
                    {
                        Name = "clickCount",
                        InputType = InputType.Number
                    },
                    new InteractionInput
                    {
                        Name = "snapshotAfter",
                        InputType = InputType.Boolean
                    }
                ]
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "myresource",
            CommandName = "click",
            Arguments = JsonSerializer.SerializeToNode(new
            {
                selector = "#submit",
                clickCount = 2,
                snapshotAfter = true
            })
        }).DefaultTimeout();

        Assert.True(response.Success);
        Assert.NotNull(capturedArguments);
        Assert.Equal("#submit", capturedArguments.GetString("selector"));
        Assert.Equal(2, capturedArguments.GetInt32("clickCount"));
        Assert.True(capturedArguments.GetBoolean("snapshotAfter"));
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_UnknownJsonArgument_ReturnsFailure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myresource"));
        custom.WithCommand(
            name: "click",
            displayName: "Click",
            executeCommand: _ =>
            {
                executed = true;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "selector",
                        InputType = InputType.Text
                    }
                ]
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "myresource",
            CommandName = "click",
            Arguments = JsonSerializer.SerializeToNode(new
            {
                selecter = "#submit"
            })
        }).DefaultTimeout();

        Assert.False(response.Success);
        Assert.False(executed);
        Assert.Equal("Unknown argument 'selecter' for command 'click'.", response.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_MapsJsonArrayArgumentsToInteractionInputsByOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myresource"));
        custom.WithCommand(
            name: "click",
            displayName: "Click",
            executeCommand: context =>
            {
                capturedArguments = context.Arguments;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "selector",
                        InputType = InputType.Text
                    },
                    new InteractionInput
                    {
                        Name = "clickCount",
                        InputType = InputType.Number
                    },
                    new InteractionInput
                    {
                        Name = "snapshotAfter",
                        InputType = InputType.Boolean
                    }
                ]
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "myresource",
            CommandName = "click",
            Arguments = JsonSerializer.SerializeToNode(new object[] { "#submit", 2, true })
        }).DefaultTimeout();

        Assert.True(response.Success);
        Assert.NotNull(capturedArguments);
        Assert.Equal("#submit", capturedArguments.GetString("selector"));
        Assert.Equal(2, capturedArguments.GetInt32("clickCount"));
        Assert.True(capturedArguments.GetBoolean("snapshotAfter"));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_ReturnsLoadedArgumentInputsWhenRequested()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var custom = builder.AddResource(new CustomResource("myresource"));
        custom.WithCommand(
            name: "configure",
            displayName: "Configure",
            executeCommand: _ => Task.FromResult(CommandResults.Success()),
            commandOptions: new CommandOptions
            {
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "browser",
                        InputType = InputType.Choice,
                        Required = true,
                        Options =
                        [
                            new("Chrome", "Chrome")
                        ]
                    },
                    new InteractionInput
                    {
                        Name = "profile",
                        InputType = InputType.Choice,
                        DynamicLoading = new InputLoadOptions
                        {
                            DependsOnInputs = ["browser"],
                            LoadCallback = context =>
                            {
                                context.Input.Options =
                                [
                                    new($"{context.AllInputs.GetString("browser")}-Default", "Default profile")
                                ];
                                return Task.CompletedTask;
                            }
                        }
                    }
                ]
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "myresource",
            CommandName = "configure",
            ValidateOnly = true,
            ReturnArgumentInputs = true,
            Arguments = JsonSerializer.SerializeToNode(new { browser = "Chrome" })
        }).DefaultTimeout();

        Assert.True(response.Success);
        var profileInput = Assert.Single(response.ArgumentInputs!, input => input.Name == "profile");
        Assert.Equal("Chrome-Default", Assert.Single(profileInput.Options!).Key);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_AllowsArgumentsEnabledByDynamicLoading()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myresource"));
        custom.WithCommand(
            name: "configure",
            displayName: "Configure",
            executeCommand: context =>
            {
                capturedArguments = context.Arguments;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "category",
                        InputType = InputType.Choice,
                        Required = true,
                        Options =
                        [
                            new("fruit", "Fruit")
                        ]
                    },
                    new InteractionInput
                    {
                        Name = "item",
                        InputType = InputType.Choice,
                        Required = true,
                        Disabled = true,
                        DynamicLoading = new InputLoadOptions
                        {
                            DependsOnInputs = ["category"],
                            LoadCallback = context =>
                            {
                                context.Input.Disabled = context.AllInputs.GetString("category") is not "fruit";
                                context.Input.Options =
                                [
                                    new("banana", "Banana")
                                ];
                                return Task.CompletedTask;
                            }
                        }
                    }
                ]
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "myresource",
            CommandName = "configure",
            Arguments = JsonSerializer.SerializeToNode(new { category = "fruit", item = "banana" })
        }).DefaultTimeout();

        Assert.True(response.Success, response.Message);
        Assert.NotNull(capturedArguments);
        Assert.Equal("banana", capturedArguments.GetString("item"));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_TooManyJsonArrayArguments_ReturnsFailure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myresource"));
        custom.WithCommand(
            name: "click",
            displayName: "Click",
            executeCommand: context =>
            {
                executed = true;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "selector",
                        InputType = InputType.Text
                    }
                ]
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "myresource",
            CommandName = "click",
            Arguments = JsonSerializer.SerializeToNode(new[] { "#submit", "extra" })
        }).DefaultTimeout();

        Assert.False(response.Success);
        Assert.False(executed);
        Assert.Equal("Command 'click' accepts 1 argument(s), but 2 were provided.", response.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_ValidateOnlyWithInvalidArguments_ReturnsValidationErrors()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myresource"));
        custom.WithCommand(
            name: "validate",
            displayName: "Validate",
            executeCommand: context =>
            {
                executed = true;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "target",
                        InputType = InputType.Text
                    }
                ],
                ValidateArguments = context =>
                {
                    var target = context.Inputs.Single(argument => argument.Name == "target");
                    context.AddValidationError(target, "Target must not be prod.");

                    return Task.CompletedTask;
                }
            });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "myresource",
            CommandName = "validate",
            ValidateOnly = true,
            Arguments = JsonSerializer.SerializeToNode(new
            {
                target = "prod"
            })
        }).DefaultTimeout();

        Assert.False(response.Success);
        Assert.False(executed);
        var validationError = Assert.Single(response.ValidationErrors);
        Assert.Equal("target", validationError.ArgumentName);
        Assert.Equal("Target must not be prod.", validationError.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_ValidateOnlyWithMissingResource_ReturnsNotFound()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "missing-resource",
            CommandName = "validate",
            ValidateOnly = true
        }).DefaultTimeout();

        Assert.False(response.Success);
        Assert.Equal("Resource 'missing-resource' not found.", response.Message);
    }

    [Fact]
    public async Task ExecuteResourceCommandAsync_ValidateOnlyWithMissingCommand_ReturnsNotFound()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var response = await target.ExecuteResourceCommandAsync(new ExecuteResourceCommandRequest
        {
            ResourceName = "myresource",
            CommandName = "missing-command",
            ValidateOnly = true
        }).DefaultTimeout();

        Assert.False(response.Success);
        Assert.Equal("Command 'missing-command' not available for resource 'myresource'.", response.Message);
    }

    [Fact]
    public async Task GetDashboardUrlsAsync_ReturnsUnhealthy_WhenDashboardResourceIsAbsent()
    {
        // When the dashboard is disabled, there is no dashboard resource in the app model.
        // The method must return promptly rather than waiting forever for a resource event
        // that will never arrive.
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DisableDashboard = true,
            outputHelper);

        using var app = builder.Build();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.GetDashboardUrlsAsync().DefaultTimeout();

        Assert.False(result.DashboardHealthy);
        Assert.Null(result.BaseUrlWithLoginToken);
    }

    [Fact]
    public async Task WaitForResourceAsync_ReturnsNotFound_WhenResourceDoesNotExist()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.AddResource(new CustomResource("myresource"));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.WaitForResourceAsync(new WaitForResourceRequest
        {
            ResourceName = "nonexistent",
            Status = "up",
            TimeoutSeconds = 10
        }).DefaultTimeout();

        Assert.False(result.Success);
        Assert.True(result.ResourceNotFound);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WaitForResourceAsync_ReturnsNotFound_ForBadInstanceName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);

        var resourceWithReplicas = builder.AddResource(new CustomResource("myresource"));
        resourceWithReplicas.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myresource-abc123", "abc123", 0),
            new DcpInstance("myresource-def456", "def456", 1)
        ]));

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            app.Services.GetRequiredService<IConfiguration>(),
            app.Services.GetRequiredService<ProfilingTelemetry>(),
            app.Services);

        var result = await target.WaitForResourceAsync(new WaitForResourceRequest
        {
            ResourceName = "myresource-nonexistent",
            Status = "up",
            TimeoutSeconds = 10
        }).DefaultTimeout();

        Assert.False(result.Success);
        Assert.True(result.ResourceNotFound);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task GetAppHostInformationAsync_ReturnsCliLogFilePath_WhenConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Path"] = "/path/to/apphost.csproj",
                [KnownConfigNames.CliProcessId] = "5678",
                [KnownConfigNames.CliProcessStarted] = "1783180199",
                [KnownConfigNames.CliProcessStartedStable] = "1783180250",
                [KnownConfigNames.CliLogFilePath] = "/logs/cli_20260516T120000_abcd1234.log"
            })
            .Build();

        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<ProfilingTelemetry>()
            .BuildServiceProvider();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            configuration,
            services.GetRequiredService<ProfilingTelemetry>(),
            services);

        var result = await target.GetAppHostInformationAsync().DefaultTimeout();

        Assert.Equal("/logs/cli_20260516T120000_abcd1234.log", result.CliLogFilePath);
        Assert.Equal(5678, result.CliProcessId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783180199), result.CliStartedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1783180250), result.CliStableStartedAt);
        Assert.NotNull(result.StartedAt);
        Assert.NotNull(result.StableStartedAt);
        Assert.True(ProcessStartTimeHelper.AreClose(
            ProcessStartTimeHelper.GetCurrentProcessRuntimeStartTimeUnixSeconds(),
            result.StartedAt.Value.ToUnixTimeSeconds(),
            TimeSpan.FromSeconds(1)));
        Assert.Equal(ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(Environment.ProcessId), result.StableStartedAt.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task GetAppHostInfoAsync_IncludesCliLogFilePath()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Path"] = "/path/to/apphost.csproj",
                [KnownConfigNames.CliLogFilePath] = "/logs/cli_session.log"
            })
            .Build();

        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<ProfilingTelemetry>()
            .BuildServiceProvider();

        var target = new AuxiliaryBackchannelRpcTarget(
            NullLogger<AuxiliaryBackchannelRpcTarget>.Instance,
            configuration,
            services.GetRequiredService<ProfilingTelemetry>(),
            services);

        var result = await target.GetAppHostInfoAsync().DefaultTimeout();

        Assert.Equal("/logs/cli_session.log", result.CliLogFilePath);
    }
}
