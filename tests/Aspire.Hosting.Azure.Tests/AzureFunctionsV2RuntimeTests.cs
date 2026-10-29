// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Verify authoritative Functions launch metadata after a real coordinated build.
#pragma warning disable ASPIREPIPELINES003 // Exercise SDK container publication against the local runtime.
#pragma warning disable ASPIRECONTAINERRUNTIME001 // Inspect and clean up the test-owned image.

using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Azure.Storage.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureFunctionsV2RuntimeTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    [RequiresTools(["func"])]
    public async Task CSharpFunctionsHostUsesCoordinatedOutputAndQueueBinding()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = AzureFunctionsTestHelpers.CreateRuntimeProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var queues = storage.AddQueues("queue");
        var functions = builder.AddAzureFunctions("functions", projectPath)
            .WithBuildEnvironment("BUILD_FLAVOR", "functions-v2")
            .WithHostStorage(storage)
            .WithReference(queues)
            .WaitFor(storage)
            .WithHttpHealthCheck("/api/health");
        using var app = builder.Build();

        try
        {
            using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                await app.StartAsync(startup.Token);
            }

            using (var ready = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                await app.Services.GetRequiredService<ResourceNotificationService>()
                    .WaitForResourceHealthyAsync(functions.Resource.Name, ready.Token);
            }

            await VerifyHttpAndQueueBindingAsync(app, functions.Resource.Name, queues.Resource, "functions-v2");

            var metadata = functions.Resource.GetProjectMetadata();
            Assert.True(metadata.SuppressBuild);
            Assert.Equal("functions-v2", metadata.BuildEnvironment["BUILD_FLAVOR"]);
            Assert.Equal(Path.GetDirectoryName(projectPath), metadata.BuildWorkingDirectory);
            Assert.Contains("functions-v2", functions.Resource.WorkingDirectory, StringComparison.Ordinal);
            Assert.Equal(["built"], File.ReadAllLines(Path.Combine(Path.GetDirectoryName(projectPath)!, "build-count.txt")));

            var arguments = await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("cmd", Path.GetFileNameWithoutExtension(functions.Resource.Command), ignoreCase: true);
                Assert.Equal(["/C", "func", "start"], arguments.Take(3));
            }
            else
            {
                Assert.Equal("func", Path.GetFileNameWithoutExtension(functions.Resource.Command));
                Assert.Equal("start", arguments[0]);
            }

            var launch = await functions.Resource.CreateLaunchConfigurationAsync("NoDebug", TestContext.Current.CancellationToken);
            var json = JsonSerializer.SerializeToElement(launch);
            Assert.Equal("azure-functions-with-external-build.v1", json.GetProperty("type").GetString());
            Assert.Equal(projectPath, json.GetProperty("project_path").GetString());
            Assert.True(json.GetProperty("suppress_build").GetBoolean());
            Assert.Equal(metadata.BuildWorkingDirectory, json.GetProperty("build_working_directory").GetString());
            Assert.Equal("functions-v2", json.GetProperty("build_environment").GetProperty("BUILD_FLAVOR").GetString());
            var configuration = builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
            if (configuration is not null)
            {
                Assert.Equal(configuration, json.GetProperty("build_configuration").GetString());
            }

            await Verify(json.GetRawText(), "json")
                .AddScrubber(text => text
                    .Replace(JsonEncodedText.Encode(projectPath).ToString(), "Functions.csproj")
                    .Replace(JsonEncodedText.Encode(metadata.BuildWorkingDirectory!).ToString(), "Functions")
                    .Replace($"\"build_configuration\":\"{configuration}\"", "\"build_configuration\":\"Configuration\""));
        }
        catch
        {
            workspace.Preserve();
            throw;
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await app.StopAsync(stop.Token);
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
    public async Task CSharpFunctionsSdkImageStartsFunctionsHostAndQueueBinding()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = AzureFunctionsTestHelpers.CreateRuntimeProject(workspace.Path);
        var archivePath = Path.Combine(workspace.Path, "functions.tar.gz");
        var imageName = $"localhost/functions-v2-{Guid.NewGuid():N}";
        var imageReference = $"{imageName}:sdk-test";
        using var publisher = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper);
        var functions = publisher.AddAzureFunctions("functions", projectPath)
            .WithBuildEnvironment("BUILD_FLAVOR", "functions-v2-image")
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.LocalImageName = imageName;
                context.LocalImageTag = "sdk-test";
                context.OutputPath = archivePath;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });
        using var publishApp = publisher.Build();
        using var build = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var runtime = await publishApp.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(build.Token);

        try
        {
            await publishApp.Services.GetRequiredService<IResourceContainerImageManager>()
                .BuildImageAsync(functions.Resource, build.Token);
            Assert.Equal([imageReference], TestContainerImageArchive.ReadDockerImageReferences(archivePath));
            await AzureFunctionsTestHelpers.RunContainerRuntimeAsync(runtime, ["image", "load", "--input", archivePath], build.Token);
            var inspection = await runtime.InspectImageConfigAsync(imageReference, build.Token);
            Assert.True(inspection.TryGetConfig(out var imageConfig), inspection.ErrorMessage);
            Assert.True(
                imageConfig.Entrypoint.Concat(imageConfig.Command).Any(argument =>
                    argument.Contains("azure-functions-host", StringComparison.Ordinal) ||
                    argument.Contains("start_nonappservice.sh", StringComparison.Ordinal)),
                "The published image must start the Functions host, not the isolated worker directly.");

            using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
            var storage = builder.AddAzureStorage("storage").RunAsEmulator();
            var queues = storage.AddQueues("queue");
            builder.AddContainer("functions", imageName, "sdk-test")
                .WithHttpEndpoint(targetPort: 8080)
                .WithEnvironment("FUNCTIONS_WORKER_RUNTIME", "dotnet-isolated")
                .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
                .WithEnvironment(context =>
                {
                    ((IResourceWithAzureFunctionsConfig)storage.Resource).ApplyAzureFunctionsConfiguration(context.EnvironmentVariables, "AzureWebJobsStorage");
                    ((IResourceWithAzureFunctionsConfig)queues.Resource).ApplyAzureFunctionsConfiguration(context.EnvironmentVariables, "queue");
                })
                .WaitFor(storage)
                .WithHttpHealthCheck("/api/health");
            using var app = builder.Build();
            try
            {
                using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                {
                    await app.StartAsync(startup.Token);
                }

                using (var ready = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                {
                    await app.Services.GetRequiredService<ResourceNotificationService>()
                        .WaitForResourceHealthyAsync("functions", ready.Token);
                }

                await VerifyHttpAndQueueBindingAsync(app, "functions", queues.Resource, "functions-v2-image");
                await Verify(new { imageConfig.Entrypoint, imageConfig.Command, imageConfig.WorkingDirectory });
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await app.StopAsync(stop.Token);
            }
        }
        catch
        {
            workspace.Preserve();
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await runtime.RemoveImageAsync(imageReference, cleanup.Token);
            }
            catch (Exception exception)
            {
                outputHelper.WriteLine($"Could not remove test-owned image {imageReference}: {exception.Message}");
            }
        }
    }

    private static async Task VerifyHttpAndQueueBindingAsync(
        DistributedApplication app,
        string resourceName,
        AzureQueueStorageResource queues,
        string buildFlavor)
    {
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var client = app.CreateHttpClient(resourceName);
        Assert.Equal($"{buildFlavor}|", await client.GetStringAsync("/api/health", operation.Token));
        using var response = await client.PostAsync("/api/enqueue", new StringContent("functions-v2-roundtrip"), operation.Token);
        response.EnsureSuccessStatusCode();

        var queueClient = new QueueClient(
            await queues.ConnectionStringExpression.GetValueAsync(operation.Token),
            "roundtrip",
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        var message = (await queueClient.ReceiveMessageAsync(cancellationToken: operation.Token)).Value;
        Assert.NotNull(message);
        Assert.Equal("functions-v2-roundtrip", message.MessageText);
        await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, operation.Token);
    }
}
