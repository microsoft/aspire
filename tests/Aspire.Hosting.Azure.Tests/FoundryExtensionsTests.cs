// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Foundry;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Hosting.Azure.Tests;

public class FoundryExtensionsTests
{
    [Fact]
    public void AddFoundry_ShouldAddResourceToBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        Assert.NotNull(resourceBuilder);
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.Equal("myAIFoundry", resource.Name);
    }

    [Fact]
    public void AddDeployment_ShouldAddDeploymentToResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        var deploymentBuilder = resourceBuilder.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        Assert.NotNull(deploymentBuilder);
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        var deployment = Assert.Single(resource.Deployments);
        Assert.Equal("deployment1", deployment.Name);
        Assert.Equal("deployment1", deployment.DeploymentName);
        Assert.Equal("gpt-4", deployment.ModelName);
        Assert.Equal("1.0", deployment.ModelVersion);
        Assert.Equal("OpenAI", deployment.Format);
    }

    [Fact]
    public void WithProperties_ShouldApplyConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        var deploymentBuilder = resourceBuilder.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        bool configured = false;
        deploymentBuilder.WithProperties(d =>
        {
            configured = true;
            d.ModelName = "changed";
        });
        Assert.True(configured);
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        var deployment = Assert.Single(resource.Deployments);
        Assert.Equal("changed", deployment.ModelName);
    }

    [Fact]
    public void AddFoundry_ConnectionString_IsCorrect()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        // The connection string should reference the aiFoundryApiEndpoint output
        var expected = $"Endpoint={resource.Endpoint.ValueExpression};EndpointAIInference={resource.AIFoundryApiEndpoint.ValueExpression}models";
        var connectionString = resource.ConnectionStringExpression.ValueExpression;
        Assert.Equal(expected, connectionString);
    }

    [Fact]
    public async Task RunAsFoundryLocal_SetsIsEmulator()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.False(resource.IsEmulator);
        Assert.Null(resource.ApiKey);

        var localBuilder = resourceBuilder.RunAsFoundryLocal();

        var localResource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.True(localResource.IsEmulator);

        await using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        Assert.Contains(app.Services.GetServices<global::Microsoft.Extensions.Hosting.IHostedService>(), service => service is FoundryLocalLifecycleService);

        // Wait until it's not in Starting state anymore (started or failed whether the Foundry Local service is setup or not)
        await rns.WaitForResourceAsync(resource.Name, [KnownResourceStates.FailedToStart, KnownResourceStates.Running], cts.Token);

        Assert.Equal(FoundryLocalService.ApiKey, localResource.ApiKey);

        await app.StopAsync(cts.Token);

        Assert.False(FoundryLocalService.IsServiceRunning);
    }

    [Fact]
    public void FoundryLocalService_TryParseModelId_ParsesModelInfoOutput()
    {
        var output = """
            Alias                          Device     Task           File Size    License      Model ID
            phi-3.5-mini                   GPU        chat           2.16 GB      MIT          Phi-3.5-mini-instruct-generic-gpu:1
            """;

        Assert.True(FoundryLocalService.TryParseModelId(output, out var modelId));
        Assert.Equal("Phi-3.5-mini-instruct-generic-gpu:1", modelId);
    }

    [Fact]
    public void FoundryLocalService_TryParseModelId_IgnoresDiagnosticOutputBeforeTable()
    {
        var output = """
            [15:12:56 ERR] Exception fetching models from Azure Foundry catalog
            Model management service is running on http://127.0.0.1:54597/openai/status
            Alias                          Device     Task           File Size    License      Model ID
            phi-3.5-mini                   GPU        chat           2.16 GB      MIT          Phi-3.5-mini-instruct-generic-gpu:1
            """;

        Assert.True(FoundryLocalService.TryParseModelId(output, out var modelId));
        Assert.Equal("Phi-3.5-mini-instruct-generic-gpu:1", modelId);
    }

    [Theory]
    [InlineData("""
        Commands:
          service  Commands to start and stop the Foundry Local service
        """, "service")]
    [InlineData("""
        Commands:
          server   Start, stop, restart, inspect, and troubleshoot the local Foundry daemon
        """, "server")]
    public void FoundryLocalService_DetermineDaemonVerb_DetectsInstalledCli(string helpOutput, string expectedVerb)
    {
        Assert.Equal(expectedVerb, FoundryLocalService.DetermineDaemonVerb(helpOutput));
    }

    [Fact]
    public void FoundryLocalService_DetermineDaemonVerb_RejectsUnsupportedCli()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => FoundryLocalService.DetermineDaemonVerb("Commands: model, chat"));

        Assert.Equal(
            "The installed Foundry CLI does not expose a 'server' or 'service' command. Update Foundry Local and ensure the 'foundry' command on PATH is the expected installation.",
            exception.Message);
    }

    [Theory]
    [InlineData(
        """{"running":true,"webUrls":["http://127.0.0.1:55829"],"port":55829}""",
        "http://127.0.0.1:55829/")]
    [InlineData(
        "success: Server ready (http://127.0.0.1:55829)",
        "http://127.0.0.1:55829/")]
    public void FoundryLocalService_TryParseServerEndpoint_ParsesCurrentCliOutput(string output, string expectedEndpoint)
    {
        Assert.True(FoundryLocalService.TryParseServerEndpoint(output, out var endpoint));
        Assert.Equal(new Uri(expectedEndpoint), endpoint);
    }

    [Fact]
    public async Task FoundryLocalService_ServerStartupCompletesWhenDaemonKeepsOutputStreamsOpen()
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory(".foundry-daemon-test");
        var daemonPidPath = Path.Combine(temporaryDirectory.FullName, "daemon.pid");
        var options = new RemoteInvokeOptions
        {
            Start = false
        };
        options.StartInfo.RedirectStandardOutput = true;
        options.StartInfo.RedirectStandardError = true;

        using var handle = RemoteExecutor.Invoke(RunDaemonizingCli, daemonPidPath, options);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var result = await FoundryLocalService.RunProcessAsync(
                handle.Process,
                "test server start",
                onOutput: null,
                cancellation.Token,
                stopReadingAfterProcessExit: true,
                outputCompletionPredicate: line => FoundryLocalService.TryParseServerEndpoint(line, out _));

            Assert.Equal(
                """{"running":true,"webUrls":["http://127.0.0.1:55829"],"port":55829}""",
                result.Output);
        }
        finally
        {
            if (File.Exists(daemonPidPath) &&
                int.TryParse(await File.ReadAllTextAsync(daemonPidPath), out var daemonPid))
            {
                using var daemon = Process.GetProcessById(daemonPid);
                if (!daemon.HasExited)
                {
                    daemon.Kill(entireProcessTree: true);
                    await daemon.WaitForExitAsync();
                }
            }

            Directory.Delete(temporaryDirectory.FullName, recursive: true);
        }

        static void RunDaemonizingCli(string daemonPidPath)
        {
            // Emulate `foundry server start`: the CLI parent reports the endpoint and exits while
            // the daemon child keeps the parent's redirected stdout and stderr handles open.
            var daemonStartInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/d /s /c \"ping -n 300 127.0.0.1 > nul\"")
                : new ProcessStartInfo("sleep", "300");
            daemonStartInfo.UseShellExecute = false;

            using var daemon = Process.Start(daemonStartInfo) ??
                throw new InvalidOperationException("Failed to start the test daemon process.");
            File.WriteAllText(daemonPidPath, daemon.Id.ToString());
            Console.WriteLine("""{"running":true,"webUrls":["http://127.0.0.1:55829"],"port":55829}""");
        }
    }

    [Fact]
    public async Task FoundryLocalService_ServerStartupFailsWhenCliExitsWithoutEndpoint()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        if (OperatingSystem.IsWindows())
        {
            process.StartInfo.ArgumentList.Add("/d");
            process.StartInfo.ArgumentList.Add("/s");
            process.StartInfo.ArgumentList.Add("/c");
            process.StartInfo.ArgumentList.Add("echo startup failed>&2&exit /b 42");
        }
        else
        {
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("printf 'startup failed\\n' >&2; exit 42");
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FoundryLocalService.RunProcessAsync(
                process,
                "test server start",
                onOutput: null,
                cancellation.Token,
                stopReadingAfterProcessExit: true,
                outputCompletionPredicate: line => FoundryLocalService.TryParseServerEndpoint(line, out _)));

        Assert.Equal(
            "Foundry CLI command 'test server start' exited before producing required output with exit code 42: startup failed",
            exception.Message);
    }

    [Theory]
    [InlineData(
        """{"model":{"id":"Phi-4-mini-instruct-generic-gpu:5","cached":true}}""",
        "Phi-4-mini-instruct-generic-gpu:5",
        true)]
    [InlineData(
        """{"model":{"id":"Phi-4-mini-instruct-generic-gpu:5","cached":false}}""",
        "Phi-4-mini-instruct-generic-gpu:5",
        false)]
    public void FoundryLocalService_TryParseModelInfo_ParsesCurrentCliOutput(string output, string expectedModelId, bool expectedCached)
    {
        Assert.True(FoundryLocalService.TryParseModelInfo(output, out var modelId, out var cached));
        Assert.Equal(expectedModelId, modelId);
        Assert.Equal(expectedCached, cached);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [SkipOnPlatform(TestPlatforms.Windows, "The synthetic Foundry CLI uses a POSIX shell script.")]
    public void RunAsFoundryLocal_PreparesCachedAndUncachedModelsInOrder(bool cached)
    {
        RemoteExecutor.Invoke(RunModelPreparationScenario, cached.ToString()).Dispose();

        static void RunModelPreparationScenario(string cachedValue)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var cached = bool.Parse(cachedValue);
            var temporaryDirectory = Directory.CreateTempSubdirectory(".foundry-model-test");
            var commandLogPath = Path.Combine(temporaryDirectory.FullName, "commands.log");
            var executablePath = Path.Combine(temporaryDirectory.FullName, "foundry");
            var originalPath = Environment.GetEnvironmentVariable("PATH");

            try
            {
                File.WriteAllText(executablePath, """
                    #!/bin/sh
                    printf '%s\n' "$*" >> "$FOUNDRY_FAKE_LOG"
                    if [ "$1" = "--help" ]; then
                      printf '%s\n' 'Commands:' '  server   Start, stop, restart, inspect, and troubleshoot the local Foundry daemon'
                      exit 0
                    fi
                    if [ "$1 $2" = "model info" ]; then
                      printf '{"model":{"id":"Phi-4-mini-instruct-generic-gpu:5","cached":%s}}\n' "$FOUNDRY_FAKE_CACHED"
                      exit 0
                    fi
                    if [ "$1 $2" = "model download" ]; then
                      printf '%s\n' \
                        'Model ID' \
                        'Phi-4-mini-instruct-generic-gpu:5'
                      exit 0
                    fi
                    if [ "$1 $2" = "model load" ]; then
                      exit 0
                    fi
                    exit 1
                    """);
                File.SetUnixFileMode(
                    executablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                Environment.SetEnvironmentVariable("PATH", $"{temporaryDirectory.FullName}{Path.PathSeparator}{originalPath}");
                Environment.SetEnvironmentVariable("FOUNDRY_FAKE_LOG", commandLogPath);
                Environment.SetEnvironmentVariable("FOUNDRY_FAKE_CACHED", cached.ToString().ToLowerInvariant());

                using var builder = TestDistributedApplicationBuilder.Create();
                var foundry = builder.AddFoundry("foundry");
                var deployment = foundry.AddDeployment("deployment", "gpt-4", "1.0", "OpenAI");
                foundry.RunAsFoundryLocal();
                using var app = builder.Build();
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                builder.Eventing
                    .PublishAsync(new ResourceReadyEvent(foundry.Resource, app.Services), cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
                app.ResourceNotifications
                    .WaitForResourceAsync(deployment.Resource.Name, KnownResourceStates.Running, cancellation.Token)
                    .GetAwaiter()
                    .GetResult();

                Assert.Equal("Phi-4-mini-instruct-generic-gpu:5", deployment.Resource.LocalModelId);
                Assert.Equal(
                    cached
                        ? [
                            "--help",
                            "model info gpt-4 --output json",
                            "model load Phi-4-mini-instruct-generic-gpu:5"
                        ]
                        : [
                            "--help",
                            "model info gpt-4 --output json",
                            "model download gpt-4",
                            "model load Phi-4-mini-instruct-generic-gpu:5"
                        ],
                    File.ReadAllLines(commandLogPath));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                Environment.SetEnvironmentVariable("FOUNDRY_FAKE_LOG", null);
                Environment.SetEnvironmentVariable("FOUNDRY_FAKE_CACHED", null);
                Directory.Delete(temporaryDirectory.FullName, recursive: true);
            }
        }
    }

    [Fact]
    public void FoundryLocalService_TryParseModelIds_ParsesLoadedModels()
    {
        Assert.True(FoundryLocalService.TryParseModelIds(
            """["Phi-4-mini-instruct-generic-gpu:5","qwen3-4b-generic-cpu:3"]""",
            out var modelIds));

        Assert.Equal(
            ["Phi-4-mini-instruct-generic-gpu:5", "qwen3-4b-generic-cpu:3"],
            modelIds);
    }

    [Theory]
    [InlineData("phi-4", true)]
    [InlineData("qwen3", false)]
    public async Task FoundryLocalService_IsModelLoadedAsync_ChecksLoadedModelsEndpoint(string modelId, bool expected)
    {
        var handler = new CallbackHttpMessageHandler((_, request) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri("http://windows-host:5273/models/loaded"), request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""["Phi-4"]""")
            };
        });
        using var httpClient = new HttpClient(handler);

        var result = await FoundryLocalService.IsModelLoadedCoreAsync(
            new Uri("http://windows-host:5273/"),
            modelId,
            httpClient,
            _ => throw new InvalidOperationException("The legacy CLI fallback should not run after a successful modern endpoint response."),
            CancellationToken.None);

        Assert.Equal(expected, result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FoundryLocalService_IsModelLoadedAsync_FallsBackToLegacyEndpointAfterNotFound()
    {
        var handler = new CallbackHttpMessageHandler((attempt, request) =>
        {
            var expectedUri = attempt switch
            {
                1 => new Uri("http://windows-host:5273/models/loaded"),
                2 => new Uri("http://windows-host:5273/openai/loadedmodels"),
                _ => throw new InvalidOperationException($"Unexpected request attempt {attempt}.")
            };
            Assert.Equal(expectedUri, request.RequestUri);

            return attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""["Phi-4"]""")
                };
        });
        using var httpClient = new HttpClient(handler);

        var result = await FoundryLocalService.IsModelLoadedAsync(
            new Uri("http://windows-host:5273/"),
            "Phi-4",
            httpClient,
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task FoundryLocalService_IsModelLoadedAsync_FallsBackToLegacyCliForVersionedModelId()
    {
        var handler = new CallbackHttpMessageHandler((attempt, _) =>
            attempt is 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""["Phi-4-mini-instruct"]""")
                });
        using var httpClient = new HttpClient(handler);
        var legacyFallbackCalled = false;

        var result = await FoundryLocalService.IsModelLoadedCoreAsync(
            new Uri("http://windows-host:5273/"),
            "Phi-4-mini-instruct-generic-gpu:5",
            httpClient,
            _ =>
            {
                legacyFallbackCalled = true;
                return Task.FromResult("Phi-4-mini-instruct-generic-gpu:5");
            },
            CancellationToken.None);

        Assert.True(result);
        Assert.True(legacyFallbackCalled);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task FoundryLocalService_IsModelLoadedAsync_ReturnsFalseForUnsuccessfulResponse()
    {
        var handler = new CallbackHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handler);

        var result = await FoundryLocalService.IsModelLoadedAsync(
            new Uri("http://windows-host:5273/"),
            "Phi-4",
            httpClient,
            CancellationToken.None);

        Assert.False(result);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FoundryLocalHealthCheck_UsesOpenAiModelsEndpoint()
    {
        var handler = new CallbackHttpMessageHandler((_, request) =>
        {
            Assert.Equal(new Uri("http://windows-host:5273/v1/models"), request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var resource = new FoundryResource("foundry", _ => { })
        {
            EmulatorServiceUri = new Uri("http://windows-host:5273/")
        };
        var healthCheck = new FoundryLocalHealthCheck(resource, new TestHttpClientFactory(handler));

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FoundryLocalHealthCheck_FallsBackToLegacyStatusEndpointAfterNotFound()
    {
        var handler = new CallbackHttpMessageHandler((attempt, request) =>
        {
            var expectedUri = attempt switch
            {
                1 => new Uri("http://windows-host:5273/v1/models"),
                2 => new Uri("http://windows-host:5273/openai/status"),
                _ => throw new InvalidOperationException($"Unexpected request attempt {attempt}.")
            };
            Assert.Equal(expectedUri, request.RequestUri);

            return new HttpResponseMessage(attempt is 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        });
        var resource = new FoundryResource("foundry", _ => { })
        {
            EmulatorServiceUri = new Uri("http://windows-host:5273/")
        };
        var healthCheck = new FoundryLocalHealthCheck(resource, new TestHttpClientFactory(handler));

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public void RunAsFoundryLocal_WithExistingEndpoint_DoesNotManageService()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("myAIFoundry")
            .RunAsFoundryLocal("http://windows-host:5273");

        Assert.True(foundry.Resource.IsEmulator);
        Assert.False(foundry.Resource.ManageLocalService);
        Assert.Equal(new Uri("http://windows-host:5273/"), foundry.Resource.EmulatorServiceUri);
        Assert.Equal("Endpoint=http://windows-host:5273/;Key=unused", foundry.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void RunAsFoundryLocal_ConfiguresBoundedHealthCheckHttpClients()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddFoundry("foundry").RunAsFoundryLocal();
        using var app = builder.Build();
        var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

        using var serviceHealthClient = httpClientFactory.CreateClient(nameof(FoundryLocalHealthCheck));
        using var modelHealthClient = httpClientFactory.CreateClient(nameof(LocalModelHealthCheck));

        Assert.Equal(TimeSpan.FromSeconds(10), serviceHealthClient.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), modelHealthClient.Timeout);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://windows-host:5273")]
    public void RunAsFoundryLocal_WithInvalidExistingEndpoint_Throws(string endpoint)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("myAIFoundry");

        var exception = Assert.Throws<ArgumentException>(() =>
            foundry.RunAsFoundryLocal(endpoint));

        Assert.Equal("endpoint", exception.ParamName);
        Assert.StartsWith("The Foundry Local endpoint must be an absolute HTTP or HTTPS URL.", exception.Message);
    }

    [Fact]
    public void RunAsFoundryLocal_DeploymentIsMarkedLocal()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        resourceBuilder.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        var localBuilder = resourceBuilder.RunAsFoundryLocal();
        var localResource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.True(localResource.IsEmulator);

        foreach (var deployment in localResource.Deployments)
        {
            Assert.True(deployment.Parent.IsEmulator);
        }
    }

    [Fact]
    public void RunAsFoundryLocal_DeploymentConnectionString_HasModelProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("myAIFoundry");
        var deployment = foundry.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");

        foundry.RunAsFoundryLocal();

        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());

        Assert.Single(resource.Deployments);

        // NB: The ModelId property is updated with the downloaded model id when the resource is starting.
        // We are only testing that the ModelName fallback is referenced in the connection string.

        Assert.Equal("{myAIFoundry.connectionString};Model=gpt-4", deployment.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void RunAsFoundryLocal_DeploymentConnectionString_UsesModelId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("myAIFoundry");
        var deployment = foundry.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        foundry.RunAsFoundryLocal();

        deployment.Resource.LocalModelId = "custom-model-id";

        Assert.Equal("{myAIFoundry.connectionString};Model=custom-model-id", deployment.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AIFoundry_DeploymentConnectionString_HasDeploymentProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("myAIFoundry");
        var deployment = foundry.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");

        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());

        Assert.Single(resource.Deployments);
        Assert.Equal("{myAIFoundry.connectionString};Deployment=deployment1", deployment.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task AddFoundry_GeneratesValidBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var foundry = builder.AddFoundry("foundry");
        var deployment1 = foundry.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        var deployment2 = foundry.AddDeployment("deployment2", "Phi-4", "1.0", "Microsoft");
        var deployment3 = foundry.AddDeployment("my-model", "Phi-4", "1.0", "Microsoft");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var manifest = await AzureManifestUtils.GetManifestWithBicep(model, foundry.Resource);

        var roles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "foundry-roles");
        var rolesManifest = await AzureManifestUtils.GetManifestWithBicep(roles, skipPreparer: true);

        await Verify(manifest.BicepText, extension: "bicep")
            .AppendContentAsFile(rolesManifest.BicepText, "bicep");
    }

    [Fact]
    public void AddProject_SetsParentFoundryForProvisioningOrdering()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var foundry = builder.AddFoundry("myAIFoundry");
        var project = foundry
            .AddProject("my-project");

        Assert.Same(foundry.Resource, project.Resource.Parent);
    }

    [Fact]
    public void AddProject_DoesNotAddDefaultContainerRegistryInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var project = builder.AddFoundry("myAIFoundry")
            .AddProject("my-project");

        Assert.DoesNotContain(builder.Resources, r => r.Name == "my-project-acr");
        Assert.Empty(builder.Resources.OfType<AzureContainerRegistryResource>());
        Assert.Null(project.Resource.ContainerRegistry);
    }

    [Fact]
    public async Task AddProject_WithPublishAsExistingFoundry_GeneratesBicepThatReferencesExistingParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddFoundry("foundry")
            .PublishAsExisting("existing-foundry", "existing-rg")
            .AddProject("project");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (_, bicepText) = await AzureManifestUtils.GetManifestWithBicep(model, project.Resource);

        Assert.Contains("resource foundry 'Microsoft.CognitiveServices/accounts@", bicepText);
        Assert.Contains("existing = {", bicepText);
        Assert.Contains("name: 'existing-foundry'", bicepText);
        Assert.Contains("scope: resourceGroup('existing-rg')", bicepText);
        Assert.DoesNotContain("kind: 'AIServices'", bicepText);
    }

    [Fact]
    public async Task AddProject_GeneratesEndpointFromParentFoundryApiEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddFoundry("foundry")
            .AddProject("project");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (_, bicepText) = await AzureManifestUtils.GetManifestWithBicep(model, project.Resource);

        await Verify(bicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddFoundry_WithPublishAsExisting_UsesStableDefaultCapabilityHostName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var foundry = builder.AddFoundry("logical-foundry")
            .PublishAsExisting("existing-foundry", "existing-rg");

        foundry.AddDeployment("chat", "gpt-4", "1.0", "OpenAI");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (_, bicepText) = await AzureManifestUtils.GetManifestWithBicep(model, foundry.Resource);

        Assert.Contains("name: 'foundry-caphost'", bicepText);
        Assert.DoesNotContain("logical-foundry-caphost", bicepText);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForFoundryResource()
    {
        // Arrange
        var aiFoundryResource = new FoundryResource("test-foundry", _ => { });
        var infrastructure = new AzureResourceInfrastructure(aiFoundryResource, "test-foundry");

        // Act - Call AddAsExistingResource twice
        var firstResult = aiFoundryResource.AddAsExistingResource(infrastructure);
        var secondResult = aiFoundryResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task WithComputeEnvironment_ResolvesExternalContainerAppReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var weatherAgent = builder.AddProject<Project>("weatheragent", launchProfileName: null)
            .WithEndpoint(targetPort: 9000, scheme: "http", name: "http", isExternal: true)
            .WithComputeEnvironment(env);

        var advisorAgent = builder.AddProject<Project>("advisoragent", launchProfileName: null)
            .WithReference(weatherAgent)
            .WaitFor(weatherAgent)
            .AsHostedAgent(project, HostedAgentProtocol.Responses, "2.0.0");

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var hostedAgent = Assert.Single(model.Resources.OfType<AzureHostedAgentResource>());
        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());
        environment.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = "example.azurecontainerapps.io";
        environment.ProvisioningTaskCompletionSource?.TrySetResult();
        SetFoundryProjectOutputs(project.Resource);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var environmentVariables = await AzureHostedAgentResource.GetResolvedEnvironmentVariablesAsync(
            builder.ExecutionContext,
            hostedAgent,
            advisorAgent.Resource,
            NullLogger<FoundryExtensionsTests>.Instance,
            cts.Token);

        Assert.Equal("https://weatheragent.example.azurecontainerapps.io", environmentVariables["services__weatheragent__http__0"]);
    }

    [Fact]
    public async Task WithComputeEnvironment_DoesNotSetReservedFoundryProjectEndpointEnvironmentVariable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var advisorAgent = builder.AddProject<Project>("advisor-agent", launchProfileName: null)
            .AsHostedAgent(project, HostedAgentProtocol.Responses, "2.0.0");

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var hostedAgent = Assert.Single(model.Resources.OfType<AzureHostedAgentResource>());
        SetFoundryProjectOutputs(project.Resource);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var environmentVariables = await AzureHostedAgentResource.GetResolvedEnvironmentVariablesAsync(
            builder.ExecutionContext,
            hostedAgent,
            advisorAgent.Resource,
            NullLogger<FoundryExtensionsTests>.Instance,
            cts.Token);

        Assert.DoesNotContain("FOUNDRY_PROJECT_ENDPOINT", environmentVariables.Keys);
    }

    [Fact]
    public async Task WithComputeEnvironment_ResolvesReferenceExpressionEnvironmentVariable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var weatherAgent = builder.AddProject<Project>("weather-agent", launchProfileName: null)
            .WithEndpoint(targetPort: 9000, scheme: "http", name: "http", isExternal: true)
            .WithComputeEnvironment(env);

        var advisorAgent = builder.AddProject<Project>("advisor-agent", launchProfileName: null)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["WEATHER_HEALTH_URL"] = ReferenceExpression.Create($"{weatherAgent.GetEndpoint("http")}/health");
            })
            .AsHostedAgent(project, HostedAgentProtocol.Responses, "2.0.0");

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var hostedAgent = Assert.Single(model.Resources.OfType<AzureHostedAgentResource>());
        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());
        environment.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = "example.azurecontainerapps.io";
        environment.ProvisioningTaskCompletionSource?.TrySetResult();
        SetFoundryProjectOutputs(project.Resource);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var environmentVariables = await AzureHostedAgentResource.GetResolvedEnvironmentVariablesAsync(
            builder.ExecutionContext,
            hostedAgent,
            advisorAgent.Resource,
            NullLogger<FoundryExtensionsTests>.Instance,
            cts.Token);

        Assert.Equal("https://weather-agent.example.azurecontainerapps.io/health", environmentVariables["WEATHER_HEALTH_URL"]);
    }

    [Fact]
    public async Task WithComputeEnvironment_ResolvesEndpointReferenceExpressionEnvironmentVariable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var weatherAgent = builder.AddProject<Project>("weather-agent", launchProfileName: null)
            .WithEndpoint(targetPort: 9000, scheme: "http", name: "http", isExternal: true)
            .WithComputeEnvironment(env);

        var advisorAgent = builder.AddProject<Project>("advisor-agent", launchProfileName: null)
            .WithEnvironment(context =>
            {
                context.EnvironmentVariables["WEATHER_HOST_AND_PORT"] = weatherAgent.GetEndpoint("http").Property(EndpointProperty.HostAndPort);
            })
            .AsHostedAgent(project, HostedAgentProtocol.Responses, "2.0.0");

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var hostedAgent = Assert.Single(model.Resources.OfType<AzureHostedAgentResource>());
        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());
        environment.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = "example.azurecontainerapps.io";
        environment.ProvisioningTaskCompletionSource?.TrySetResult();
        SetFoundryProjectOutputs(project.Resource);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var environmentVariables = await AzureHostedAgentResource.GetResolvedEnvironmentVariablesAsync(
            builder.ExecutionContext,
            hostedAgent,
            advisorAgent.Resource,
            NullLogger<FoundryExtensionsTests>.Instance,
            cts.Token);

        Assert.Equal("weather-agent.example.azurecontainerapps.io:443", environmentVariables["WEATHER_HOST_AND_PORT"]);
    }

    [Fact]
    public async Task WithComputeEnvironment_ThrowsForInternalContainerAppReference()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var weatherAgent = builder.AddProject<Project>("weather-agent", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 9000)
            .WithComputeEnvironment(env);

        var advisorAgent = builder.AddProject<Project>("advisor-agent", launchProfileName: null)
            .WithReference(weatherAgent)
            .WaitFor(weatherAgent);

        advisorAgent.AsHostedAgent(project, HostedAgentProtocol.Responses, "2.0.0");

        using var app = builder.Build();
        await AzureManifestUtils.ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var hostedAgent = Assert.Single(model.Resources.OfType<AzureHostedAgentResource>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AzureHostedAgentResource.GetResolvedEnvironmentVariablesAsync(
                builder.ExecutionContext,
                hostedAgent,
                advisorAgent.Resource,
                NullLogger<FoundryExtensionsTests>.Instance,
                default));

        Assert.Contains("Foundry hosted agent 'advisor-agent-ha'", ex.Message);
        Assert.Contains("Endpoint 'http' on resource 'weather-agent' cannot be used", ex.Message);
        Assert.Contains("internal", ex.Message);
    }

    private static void SetFoundryProjectOutputs(AzureCognitiveServicesProjectResource project)
    {
        // These tests call the deployment-time environment resolver directly. In a real publish,
        // provisioning populates the Foundry project Bicep outputs before references are resolved.
        // Seed the outputs here so BicepOutputReference.GetValueAsync does not wait for provisioning.
        project.Outputs["endpoint"] = "https://account.services.ai.azure.com/api/projects/my-project";
        project.Outputs["APPLICATION_INSIGHTS_CONNECTION_STRING"] = "";
        project.ProvisioningTaskCompletionSource?.TrySetResult();
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }

    private sealed class CallbackHttpMessageHandler(
        Func<int, HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(callback(CallCount, request));
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
