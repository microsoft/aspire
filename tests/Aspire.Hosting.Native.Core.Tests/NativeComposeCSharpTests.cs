// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Authoring;
using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using NativeIntegrations.DockerCompose;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeComposeCSharpTests
{
    [Fact]
    public async Task NativeModelProducesDeterministicSecretFreeComposeAssetsWithoutDocker()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var (context, _) = await ExportAsync(client, root.FullName);
            var first = DockerComposeGenerator.Generate(context, "compose");
            var second = DockerComposeGenerator.Generate(context, "compose");
            Assert.True(JsonNode.DeepEquals(first.Compose, second.Compose));
            Assert.True(JsonNode.DeepEquals(first.Inputs, second.Inputs));
            var actualProject = first.Compose["name"]!.GetValue<string>();
            Assert.Equal(DockerComposeGenerator.ProjectName(root.FullName, "Comparison", "compose"), actualProject);
            var build = first.Compose["services"]!["web"]!["build"]!;
            Assert.Equal(Path.Combine(root.FullName, "web"), build["context"]!.GetValue<string>());
            Assert.Equal(Path.Combine(root.FullName, "web", "Dockerfile"), build["dockerfile"]!.GetValue<string>());
            build["context"] = "/app/web";
            build["dockerfile"] = "/app/web/Dockerfile";
            first.Compose["name"] = "comparison-project";
            first.Inputs["projectName"] = "comparison-project";
            var snapshot = new JsonObject { ["compose"] = first.Compose, ["inputs"] = first.Inputs };
            await Verifier.Verify(snapshot.ToJsonString(new() { WriteIndented = true }), "json")
                .UseDirectory("Snapshots");
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("dashboard")]
    [InlineData("bind-mount")]
    [InlineData("nonsecret-default")]
    [InlineData("ports-disabled")]
    [InlineData("normalization")]
    [InlineData("multiple-environments")]
    [InlineData("excluded-resource")]
    public async Task CSharpAndTypeScriptGenerateTheSamePlanAndResolveNestedExpressions(string scenario)
    {
        var script = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_COMPOSE_TYPESCRIPT");
        if (script is null)
        {
            Assert.Skip("Supply the compiled NativeComposeComparison.mjs to run the cross-language comparison.");
        }
        var root = Directory.CreateTempSubdirectory();
        try
        {
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var (context, _) = await ExportAsync(client, root.FullName);
            var cache = context.Model["resources"]!.AsArray().Single(resource => resource!["name"]!.GetValue<string>() == "cache")!;
            var web = context.Model["resources"]!.AsArray().Single(resource => resource!["name"]!.GetValue<string>() == "web")!;
            var target = context.Model["resources"]!.AsArray().Single(resource => resource!["name"]!.GetValue<string>() == "compose")!;
            switch (scenario)
            {
                case "dashboard":
                    target["configuration"]!["publish.dockerComposeEnvironment"] =
                        """{"defaultNetworkName":"existing-network","dashboardEnabled":true,"dashboardHostPort":null,"dashboardForwardedHeaders":true}""";
                    break;
                case "bind-mount":
                    cache["mounts"]!.AsArray().Add(new JsonObject
                    {
                        ["kind"] = "bind", ["source"] = "/deployment-host/with $ spaces", ["target"] = "/config", ["readOnly"] = true
                    });
                    break;
                case "nonsecret-default":
                    context.Model["parameters"]!.AsArray().Add("message");
                    context.Model["parameterDefaults"]!.AsObject()["message"] = "literal $ configuration";
                    web["environment"]!.AsObject()["MESSAGE"] = new JsonObject { ["kind"] = "parameter", ["name"] = "message" };
                    break;
                case "ports-disabled":
                    var options = JsonNode.Parse(web["configuration"]!["publish.dockerCompose"]!.GetValue<string>())!;
                    options["ports"] = new JsonArray();
                    web["configuration"]!["publish.dockerCompose"] = options.ToJsonString();
                    break;
                case "normalization":
                    cache["name"] = "Cache.With Space";
                    cache["mounts"]![0]!["source"] = "RetainedData";
                    web["dependencies"]![0] = "Cache.With Space";
                    web["environment"]!["ConnectionStrings__cache"]!["parts"]!.AsArray()
                        .Single(part => part!["kind"]!.GetValue<string>() == "endpoint")!["resource"] = "Cache.With Space";
                    break;
                case "multiple-environments":
                    var other = target.DeepClone();
                    other["name"] = "other";
                    context.Model["resources"]!.AsArray().Add(other);
                    var isolated = cache.DeepClone();
                    isolated["name"] = "isolated";
                    isolated["computeEnvironment"] = "other";
                    isolated["configuration"]!["publish.computeEnvironment"] = "other";
                    context.Model["resources"]!.AsArray().Add(isolated);
                    break;
                case "excluded-resource":
                    context.Model["resources"]!.AsArray().Add(new JsonObject
                    {
                        ["name"] = "opaque", ["type"] = "example/opaque", ["kind"] = "resource",
                        ["configuration"] = new JsonObject { ["publish.dockerCompose"] = """{"exclude":true}""" }
                    });
                    break;
            }
            var environment = new Dictionary<string, string?>
            {
                ["ASPIRE_PARAMETER_CACHE_PASSWORD"] = "quoted ' + / : % $ \" password",
                ["ASPIRE_PARAMETER_COMPOSE_DASHBOARD_TOKEN"] = "explicit-browser-token",
                ["ASPIRE_PARAMETER_COMPOSE_DASHBOARD_OTLP_KEY"] = "explicit-otlp-key"
            };
            var plan = DockerComposeGenerator.Generate(context, "compose");
            var request = new JsonObject
            {
                ["context"] = new JsonObject
                {
                    ["model"] = context.Model.DeepClone(), ["applicationDirectory"] = context.ApplicationDirectory,
                    ["environmentName"] = context.EnvironmentName, ["outputPath"] = context.OutputPath
                },
                ["name"] = "compose",
                ["environment"] = new JsonObject(environment.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value))))
            };
            var requestPath = Path.Combine(root.FullName, "comparison.json");
            await File.WriteAllTextAsync(requestPath, request.ToJsonString(), TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_NODE") ?? "node")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(requestPath);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await stderr);
            var typescript = JsonNode.Parse(await stdout)!.AsObject();
            Assert.True(JsonNode.DeepEquals(plan.Compose, typescript["compose"]), "C# and TypeScript Compose documents differ.");
            Assert.True(JsonNode.DeepEquals(plan.Inputs, typescript["inputs"]), "C# and TypeScript input contracts differ.");
            Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(plan.Services, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }), typescript["services"]), "C# and TypeScript expected deployment states differ.");
            var resolved = DockerComposeGenerator.ResolveInputs(plan, context.Model, environment);
            Assert.Equal(typescript["resolved"]!.AsObject().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, pair.Value!.GetValue<string>())), resolved.OrderBy(pair => pair.Key, StringComparer.Ordinal));
            Assert.Equal("redis://:quoted%20'%20%2B%20%2F%20%3A%20%25%20%24%20%22%20password@cache:6379",
                "redis://:" + resolved["ASPIRE_COMPOSE_EXPR_0001"] + "@cache:6379");
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task FluentCallbacksPublishAndNativePipelineSelectsCustomTarget()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "web", "Dockerfile"), "FROM node:24-alpine\n");
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var runner = new ComposeTestRunner();
            var builder = ComposeTestApplication.Create(root.FullName, environment =>
                environment.WithRunner(runner.RunAsync).ConfigureComposeFile(file =>
                {
                    file.AddVolume("helper-data", labels: new Dictionary<string, string> { ["owned"] = "comparison" });
                    file.GetService("web").PullPolicy = "missing";
                    file.AddConfig("settings", "$LITERAL");
                    file.AddSecret("external-secret");
                }));
            builder.AddContainer("helper", "alpine:3.22")
                .PublishAsDockerComposeService((target, service) =>
                {
                    Assert.Equal("compose", target.Parent.Resource.Name);
                    service.Restart = "unless-stopped";
                    service.SetLabel("literal", "$HOME");
                    service.AddVolume("helper-data", "/helper");
                });
            var target = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var step = builder.Pipeline.AddStep("verify-publish", async context =>
            {
                Assert.True(File.Exists(Path.Combine(context.PublishingContext.OutputPath, "compose.yaml")));
                await context.AddSummaryAsync("Comparison", "Artifacts generated");
                target.SetResult();
            });
            step.DependsOn.Add("publish");
            var context = await builder.PublishAsync(client, "published", "Comparison", "verify-publish", TestContext.Current.CancellationToken);
            await target.Task;
            var document = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(context.OutputPath, "compose.yaml")))!;
            Assert.Empty(runner.Calls);
            await Verify(document.ToJsonString(new() { WriteIndented = true }), "json")
                .UseDirectory("Snapshots")
                .AddScrubber(text => text
                    .Replace(JsonEncodedText.Encode(Path.Combine(root.FullName, "web", "Dockerfile")).ToString(), "/app/web/Dockerfile")
                    .Replace(JsonEncodedText.Encode(Path.Combine(root.FullName, "web")).ToString(), "/app/web")
                    .Replace(DockerComposeGenerator.ProjectName(root.FullName, "Comparison", "compose"), "comparison-project"));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task DeployUsesPrivateEnvironmentAndVerifiesPublishedBindings()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "web", "Dockerfile"), "FROM node:24-alpine\n");
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var (context, _) = await ExportAsync(client, root.FullName);
            var runner = new ComposeTestRunner();
            var target = new DockerComposeEnvironment("compose", new()
            {
                Runner = runner.RunAsync, ParameterEnvironment = new Dictionary<string, string?> { ["ASPIRE_PARAMETER_CACHE_PASSWORD"] = "explicit-secret" }
            });
            await target.PublishAsync(context);
            Assert.Empty(runner.Calls);
            await target.DeployAsync(context);
            Assert.All(runner.Calls, call =>
            {
                Assert.Equal("docker", call.Executable);
                Assert.Equal("1", call.Options.Environment["COMPOSE_DISABLE_ENV_FILE"]);
                Assert.Equal("explicit-secret", call.Options.Environment["ASPIRE_PARAMETER_CACHE_PASSWORD"]);
            });
            Assert.All(runner.Calls.Skip(2), call =>
                Assert.Equal(OperatingSystem.IsWindows() ? "NUL" : "/dev/null", call.Arguments[4]));
            var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(context.OutputPath, "deployment-state.json")))!;
            var snapshot = new
            {
                state,
                calls = runner.Calls.Select(call => new
                {
                    executable = call.Executable,
                    arguments = call.Arguments.Select(argument => NormalizePath(argument == "NUL" ? "/dev/null" : argument)),
                    workingDirectory = NormalizePath(call.Options.WorkingDirectory),
                    timeout = call.Options.Timeout
                })
            };
            await Verify(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }), "json")
                .UseDirectory("Snapshots")
                .AddScrubber(text => text.Replace(
                    DockerComposeGenerator.ProjectName(root.FullName, "Comparison", "compose"), "comparison-project"));
            await target.DeployAsync(context);
            Assert.Equal(10, runner.Calls.Count);

            string NormalizePath(string value) => value.Replace(root.FullName, "/app", StringComparison.Ordinal)
                .Replace(Path.DirectorySeparatorChar, '/');
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task MissingInputsAndStaleArtifactsFailBeforeAnyDockerCall()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "web", "Dockerfile"), "FROM node:24-alpine\n");
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var (context, _) = await ExportAsync(client, root.FullName);
            var runner = new ComposeTestRunner();
            var target = new DockerComposeEnvironment("compose", new() { Runner = runner.RunAsync, ParameterEnvironment = new Dictionary<string, string?>() });
            await Assert.ThrowsAsync<InvalidOperationException>(() => target.DeployAsync(context));
            await target.PublishAsync(context);
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => target.DeployAsync(context));
            Assert.Equal("Set required deployment input ASPIRE_PARAMETER_CACHE_PASSWORD explicitly.", missing.Message);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task DockerFailuresRedactRawJsonAndUriEncodedSecrets()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "web", "Dockerfile"), "FROM node:24-alpine\n");
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var (context, _) = await ExportAsync(client, root.FullName);
            var secret = "secret + \" / value";
            var runner = new ComposeTestRunner { ApplyFailure = secret + " " + Uri.EscapeDataString(secret) + " " + JsonSerializer.Serialize(secret)[1..^1] };
            var target = new DockerComposeEnvironment("compose", new()
            {
                Runner = runner.RunAsync, ParameterEnvironment = new Dictionary<string, string?> { ["ASPIRE_PARAMETER_CACHE_PASSWORD"] = secret }
            });
            await target.PublishAsync(context);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => target.DeployAsync(context));
            Assert.Equal("Docker Compose apply failed (exit 1): \n[REDACTED] [REDACTED] [REDACTED]", error.Message);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task NativePipelineRunsReadyBranchesConcurrentlyAndSuppressesFailedDependents()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await ConnectAsync(host);
        var pipeline = new NativePipeline();
        var rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        foreach (var name in new[] { "left", "right" })
        {
            var step = pipeline.AddStep(name, async context =>
            {
                if (Interlocked.Increment(ref started) == 2)
                {
                    rendezvous.SetResult();
                }
                await rendezvous.Task.WaitAsync(TimeSpan.FromSeconds(5), context.CancellationToken);
                if (name == "left")
                {
                    throw new InvalidOperationException("policy rejected");
                }
            });
            step.RequiredBy.Add("publish");
        }
        var dependentRan = false;
        var dependent = pipeline.AddStep("dependent", _ => { dependentRan = true; return Task.CompletedTask; });
        dependent.DependsOn.Add("left");
        dependent.RequiredBy.Add("publish");
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync(client.Server,
            new(new JsonObject(), AppContext.BaseDirectory, AppContext.BaseDirectory, "Comparison", TestContext.Current.CancellationToken), "publish"));
        Assert.Equal("policy rejected", failure.Message);
        Assert.Equal(2, started);
        Assert.False(dependentRan);
    }

    [Fact]
    public async Task RunModeLeavesComposeEnvironmentOutOfNativeModel()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        builder.AddDockerComposeEnvironment("compose").ConfigureComposeFile(_ => throw new InvalidOperationException("Publish-only callback executed."));
        builder.AddContainer("cache", "redis:7.4-alpine").PublishAsDockerComposeService(new DockerComposeServiceOptions { Image = "publish:only" });
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await ConnectAsync(host);
        var session = await client.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        await builder.MaterializeAsync(composition, TestContext.Current.CancellationToken);
        var model = JsonNode.Parse(await composition.ExportPublishModelAsync())!;
        Assert.Equal(["cache"], model["resources"]!.AsArray().Select(resource => resource!["name"]!.GetValue<string>()));
        Assert.False(builder.Pipeline.HasCustomizations);
        await session.RetireGenerationAsync(composition);
    }

    [Fact]
    public async Task BeforeStartPipelineRunsBeforeNativeWorkloadPreparation()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        await using var host = await NativeClientTestHost.StartWithExecutorAsync(executor, new());
        var builder = DistributedApplication.CreateBuilder([$"--native-endpoint={host.Endpoint}", $"--ASPIRE_NATIVE_RPC_TOKEN={host.Token}"]);
        builder.AddContainer("cache", "redis:7.4-alpine");
        var invoked = false;
        builder.Pipeline.AddStep("startup-policy", context =>
        {
            Assert.True(context.PublishingContext.IsRunMode);
            Assert.Empty(executor.Prepared);
            invoked = true;

            return Task.CompletedTask;
        }).RequiredBy.Add("before-start");
        await using var app = builder.Build();
        await app.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(invoked);
        Assert.Single(executor.Prepared);
    }

    [Theory]
    [InlineData("""{"build":"not-an-object"}""")]
    [InlineData("""{"healthcheck":[]}""")]
    [InlineData("""{"image":null}""")]
    [InlineData("""{"ports":[{"endpoint":"http","published":70000}]}""")]
    [InlineData("""{"healthcheck":{"test":["CMD-SHELL","one","two"]}}""")]
    public async Task MalformedDeploymentConfigurationFailsExplicitly(string options)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var (context, _) = await ExportAsync(client, root.FullName);
            var resource = context.Model["resources"]!.AsArray().Single(resource => resource!["name"]!.GetValue<string>() == "web")!;
            resource["configuration"]!["publish.dockerCompose"] = options;
            var failure = Record.Exception(() => DockerComposeGenerator.Generate(context, "compose"));
            Assert.True(failure is ArgumentException or InvalidOperationException);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task NativeComputeAssignmentsRejectCrossEnvironmentReferences()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var (context, _) = await ExportAsync(client, root.FullName);
            var resources = context.Model["resources"]!.AsArray();
            var environment = resources.Single(resource => resource!["name"]!.GetValue<string>() == "compose")!.DeepClone();
            environment["name"] = "other";
            resources.Add(environment);
            var cache = resources.Single(resource => resource!["name"]!.GetValue<string>() == "cache")!;
            cache["computeEnvironment"] = "other";
            cache["configuration"]!["publish.computeEnvironment"] = "other";
            var failure = Assert.Throws<InvalidOperationException>(() => DockerComposeGenerator.Generate(context, "compose"));
            Assert.Contains("across compute environments", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"defaultNetworkName":null,"dashboardEnabled":null,"dashboardHostPort":null,"dashboardForwardedHeaders":false}""")]
    public async Task MalformedEnvironmentConfigurationFailsExplicitly(string configuration)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            await using var host = await NativeClientTestHost.StartAsync();
            await using var client = await ConnectAsync(host);
            var (context, _) = await ExportAsync(client, root.FullName);
            var environment = context.Model["resources"]!.AsArray().Single(resource => resource!["name"]!.GetValue<string>() == "compose")!;
            environment["configuration"]!["publish.dockerComposeEnvironment"] = configuration;
            Assert.Throws<ArgumentException>(() => DockerComposeGenerator.Generate(context, "compose"));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Theory]
    [InlineData("setInterval(() => {}, 1000)", false)]
    [InlineData("process.stdout.write('x'.repeat(1024 * 1024 + 1)); setInterval(() => {}, 1000)", true)]
    public async Task ToolExecutionBoundsTimeoutAndCombinedOutput(string script, bool exceedsOutput)
    {
        var node = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_NODE");
        if (node is null)
        {
            Assert.Skip("Supply Node.js for real process timeout and output-bound tests.");
        }
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DockerCommand.RunAsync(node, ["-e", script],
                new(root.FullName, new Dictionary<string, string>(), exceedsOutput ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(200),
                    TestContext.Current.CancellationToken)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Equal("Docker command was cancelled, timed out, or exceeded the bounded output limit.", failure.Message);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task ToolExecutionPreservesCallerCancellation()
    {
        var node = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SYMBOLIC_TEST_NODE");
        if (node is null)
        {
            Assert.Skip("Supply Node.js for real process cancellation tests.");
        }
        var root = Directory.CreateTempSubdirectory();
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var execution = DockerCommand.RunAsync(node, ["-e", "setInterval(() => {}, 1000)"],
                new(root.FullName, new Dictionary<string, string>(), TimeSpan.FromSeconds(30), cancellation.Token));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static Task<NativeClient> ConnectAsync(NativeClientTestHost host) =>
        NativeClient.ConnectAsync(host.Endpoint, host.Token, TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

    private static async Task<(PublishingContext Context, DistributedApplicationBuilder Builder)> ExportAsync(NativeClient client, string root)
    {
        var builder = ComposeTestApplication.Create(root);
        var session = await client.Server.OpenApplicationAsync();
        var composition = await session.StartGenerationAsync();
        await builder.MaterializeAsync(composition, TestContext.Current.CancellationToken);
        var model = JsonNode.Parse(await composition.ExportPublishModelAsync())!.AsObject();
        await session.RetireGenerationAsync(composition);

        return (new(model, Path.Combine(root, "published"), root, "Comparison", TestContext.Current.CancellationToken), builder);
    }
}
