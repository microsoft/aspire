// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

public sealed class AzureFunctionsDeploymentTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DeployNodeAndDotnetFunctionsWithStorageBindingsToAzureContainerApps()
    {
        var subscriptionId = AzureAuthenticationHelpers.TryGetSubscriptionId();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            Assert.Skip("Azure subscription not configured. Set ASPIRE_DEPLOYMENT_TEST_SUBSCRIPTION.");
        }

        if (!AzureAuthenticationHelpers.IsAzureAuthAvailable())
        {
            if (DeploymentE2ETestHelpers.IsRunningInCI)
            {
                Assert.Fail("Azure authentication not available in CI. Check OIDC configuration.");
            }

            Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(70));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        using var workspace = TemporaryWorkspace.Create(output);
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("azure-functions");
        var started = Stopwatch.StartNew();

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(linkedCts.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireBundleAsync(counter, output);
            await auto.AspireInitAsync(counter);

            foreach (var package in new[] { "Aspire.Hosting.Azure.AppContainers", "Aspire.Hosting.Azure.Functions", "Aspire.Hosting.Azure.Storage" })
            {
                await auto.TypeAsync($"aspire add {package}");
                await auto.EnterAsync();
                await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));
            }

            WriteFunctionsApps(workspace);
            await auto.RunCommandAsync(
                $"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={AspireCliShellCommandHelpers.QuoteBashArg(resourceGroupName)} && export AZURE__SUBSCRIPTIONID={AspireCliShellCommandHelpers.QuoteBashArg(subscriptionId)}",
                counter);
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(40));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Query the live Container Apps and registry. The verification script checks both
            // pushed images and runs the two binding flows concurrently so one cold start does
            // not consume the other resource's readiness budget.
            await auto.RunCommandAsync(
                "az containerapp list --subscription \"$AZURE__SUBSCRIPTIONID\" -g \"$AZURE__RESOURCEGROUP\" -o json > containerapps.json && az acr list --subscription \"$AZURE__SUBSCRIPTIONID\" -g \"$AZURE__RESOURCEGROUP\" -o json > registries.json && (set -o pipefail; python3 verify-functions.py 2>&1 | tee verify-functions.log)",
                counter, TimeSpan.FromMinutes(15));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployNodeAndDotnetFunctionsWithStorageBindingsToAzureContainerApps),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["Node Functions"] = File.ReadAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "node-functions-url.txt")).Trim(),
                    [".NET Functions"] = File.ReadAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "dotnet-functions-url.txt")).Trim()
                },
                started.Elapsed);
        }
        catch (Exception ex)
        {
            var deploymentDiagnostics = GetDeploymentDiagnostics(workspace);
            var logs = string.IsNullOrEmpty(deploymentDiagnostics)
                ? ex.StackTrace
                : $"{ex.StackTrace}{Environment.NewLine}{deploymentDiagnostics}";
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployNodeAndDotnetFunctionsWithStorageBindingsToAzureContainerApps),
                resourceGroupName, ex.Message, logs);
            throw;
        }
        finally
        {
            await DotnetProjectDeploymentHelpers.CleanupAsync(resourceGroupName, subscriptionId, output);
        }
    }

    private static void WriteFunctionsApps(TemporaryWorkspace workspace)
    {
        var root = workspace.WorkspaceRoot.FullName;
        if (!Directory.EnumerateFiles(root).Any(path =>
            string.Equals(Path.GetFileName(path), "NuGet.config", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "aspire init did not create the channel-aware NuGet.config required for the current-build package sources.");
        }

        WriteNodeFunctionsApp(root);
        WriteDotnetFunctionsApp(root);

        DotnetProjectDeploymentHelpers.ReplaceInFile(
            Path.Combine(root, "apphost.cs"),
            "builder.Build().Run();",
            """
            builder.AddAzureContainerAppEnvironment("env");
            var storage = builder.AddAzureStorage("storage");
            var blobs = storage.AddBlobs("blobs");
            storage.AddBlobContainer("node-messages");
            storage.AddBlobContainer("node-receipts");
            storage.AddBlobContainer("dotnet-receipts");
            var queues = storage.AddQueues("queues");
            storage.AddQueue("node-work");
            storage.AddQueue("dotnet-work");

            #pragma warning disable ASPIREAZUREFUNCTIONS001
            builder.AddAzureFunctionsApp("node-functions", "./node-functions", AzureFunctionsLanguage.TypeScript)
                .WithExternalHttpEndpoints()
                .WithHostStorage(storage)
                .WithReference(blobs)
                .WithReference(queues, connectionName: "workQueue");
            #pragma warning restore ASPIREAZUREFUNCTIONS001

            builder.AddDotnetAzureFunction("dotnet-functions", "./dotnet-functions/DotnetFunctions.csproj")
                .WithExternalHttpEndpoints()
                .WithHostStorage(storage)
                .WithReference(blobs, connectionName: "dotnetBlobs")
                .WithReference(queues, connectionName: "dotnetWorkQueue");

            builder.Build().Run();
            """);

        WriteVerificationScript(root);
    }

    private static void WriteNodeFunctionsApp(string root)
    {
        var functions = Directory.CreateDirectory(Path.Combine(root, "node-functions")).FullName;

        // Deliberately omit a Dockerfile and built output: deployment must generate the
        // Node Functions image, install dependencies, and compile the TypeScript fixture.
        File.WriteAllText(Path.Combine(functions, "package.json"), """
            {
              "name": "node-functions-storage-deployment-test",
              "private": true,
              "version": "1.0.0",
              "main": "dist/index.js",
              "scripts": { "build": "tsc" },
              "dependencies": { "@azure/functions": "4.7.0" },
              "devDependencies": { "@types/node": "22.15.30", "typescript": "5.8.3" }
            }
            """);
        File.WriteAllText(Path.Combine(functions, "tsconfig.json"), """
            {
              "compilerOptions": {
                "target": "ES2022",
                "module": "CommonJS",
                "outDir": "dist",
                "strict": true,
                "skipLibCheck": true
              },
              "include": ["index.ts"]
            }
            """);
        File.WriteAllText(Path.Combine(functions, "host.json"), """
            {
              "version": "2.0",
              "extensionBundle": {
                "id": "Microsoft.Azure.Functions.ExtensionBundle",
                "version": "[4.*, 5.0.0)"
              }
            }
            """);
        File.WriteAllText(Path.Combine(functions, "index.ts"), """
            import { app, input, output } from '@azure/functions';

            const blobOutput = output.storageBlob({ path: 'node-messages/{id}.txt', connection: 'blobs' });
            const queueOutput = output.storageQueue({ queueName: 'node-work', connection: 'workQueue' });
            const receiptOutput = output.storageBlob({ path: 'node-receipts/{id}.txt', connection: 'blobs' });
            const blobInput = input.storageBlob({ path: 'node-messages/{id}.txt', connection: 'blobs' });
            const receiptInput = input.storageBlob({ path: 'node-receipts/{id}.txt', connection: 'blobs' });

            app.http('write', {
                route: 'roundtrip/{id}', methods: ['POST'], authLevel: 'anonymous',
                extraOutputs: [blobOutput, queueOutput],
                handler: async (request, context) => {
                    const value = await request.text();
                    context.extraOutputs.set(blobOutput, value);
                    context.extraOutputs.set(queueOutput, { id: request.params.id, value });
                    return { jsonBody: { accepted: request.params.id } };
                }
            });

            app.storageQueue('processWork', {
                queueName: 'node-work', connection: 'workQueue',
                extraOutputs: [receiptOutput],
                handler: async (message, context) => {
                    // Queue output is {"id":"<request-id>","value":"<payload>"}.
                    // The host may deliver the decoded object or the JSON string.
                    const work = (typeof message === 'string' ? JSON.parse(message) : message) as { value: string };
                    context.extraOutputs.set(receiptOutput, work.value);
                }
            });

            app.http('read', {
                route: 'roundtrip/{id}', methods: ['GET'], authLevel: 'anonymous',
                extraInputs: [blobInput, receiptInput],
                handler: async (_request, context) => ({
                    jsonBody: {
                        direct: String(context.extraInputs.get(blobInput)),
                        queued: String(context.extraInputs.get(receiptInput))
                    }
                })
            });
            """);
    }

    private static void WriteDotnetFunctionsApp(string root)
    {
        var functions = Directory.CreateDirectory(Path.Combine(root, "dotnet-functions")).FullName;

        // This runtime-created fixture intentionally sits outside the repository's central package
        // management. Keep these versions aligned with Directory.Packages.props; restore uses the
        // channel-aware NuGet.config emitted by aspire init rather than adding another package source.
        File.WriteAllText(Path.Combine(functions, "DotnetFunctions.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AzureFunctionsVersion>v4</AzureFunctionsVersion>
                <OutputType>Exe</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
                <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.52.0" />
                <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore" Version="2.1.1" />
                <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs" Version="6.8.2" />
                <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Storage.Queues" Version="5.5.5" />
                <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.1.0" />
              </ItemGroup>
              <ItemGroup>
                <None Update="host.json">
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </None>
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(functions, "host.json"), """
            {
              "version": "2.0"
            }
            """);
        File.WriteAllText(Path.Combine(functions, "Program.cs"), """
            using Microsoft.Extensions.Hosting;

            var host = new HostBuilder()
                .ConfigureFunctionsWebApplication()
                .Build();

            await host.RunAsync();
            """);
        File.WriteAllText(Path.Combine(functions, "Functions.cs"), """
            using System.Net;
            using System.Text.Json;
            using Microsoft.Azure.Functions.Worker;
            using Microsoft.Azure.Functions.Worker.Http;

            namespace DotnetFunctions;

            public static class RoundTripFunctions
            {
                [Function("enqueue")]
                public static async Task<WriteResult> Enqueue(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "roundtrip/{id}")] HttpRequestData request,
                    string id)
                {
                    var value = await request.ReadAsStringAsync().ConfigureAwait(false) ?? string.Empty;
                    var response = request.CreateResponse(HttpStatusCode.Accepted);
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await response.WriteStringAsync(JsonSerializer.Serialize(new { accepted = id })).ConfigureAwait(false);
                    return new WriteResult
                    {
                        Response = response,
                        Message = value
                    };
                }

                [Function("processWork")]
                [BlobOutput("dotnet-receipts/{queueTrigger}.txt", Connection = "dotnetBlobs")]
                public static string ProcessWork(
                    [QueueTrigger("dotnet-work", Connection = "dotnetWorkQueue")] string message)
                {
                    return message;
                }

                [Function("read")]
                public static async Task<HttpResponseData> Read(
                    [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "roundtrip/{id}")] HttpRequestData request,
                    [BlobInput("dotnet-receipts/{id}.txt", Connection = "dotnetBlobs")] string queued)
                {
                    var response = request.CreateResponse(HttpStatusCode.OK);
                    response.Headers.Add("Content-Type", "application/json; charset=utf-8");
                    await response.WriteStringAsync(JsonSerializer.Serialize(new { queued })).ConfigureAwait(false);
                    return response;
                }
            }

            public sealed class WriteResult
            {
                [HttpResult]
                public required HttpResponseData Response { get; init; }

                [QueueOutput("dotnet-work", Connection = "dotnetWorkQueue")]
                public required string Message { get; init; }
            }
            """);
    }

    private static void WriteVerificationScript(string root)
    {
        File.WriteAllText(Path.Combine(root, "verify-functions.py"), """
            import json
            import os
            import subprocess
            import time
            import urllib.error
            import urllib.request
            import uuid
            from concurrent.futures import ThreadPoolExecutor
            from pathlib import Path

            apps = json.loads(Path('containerapps.json').read_text())
            registries = json.loads(Path('registries.json').read_text())

            def select_app(resource_name):
                matches = [app for app in apps if app['name'].startswith(resource_name)]
                assert len(matches) == 1, (
                    f'Expected one Container App for {resource_name}, found '
                    f'{[app["name"] for app in matches]} among {[app["name"] for app in apps]}')
                return matches[0]

            def inspect_app(resource_name, target_port, required_service_uris):
                app = select_app(resource_name)
                assert app.get('kind', '').lower() == 'functionapp', (
                    f'{resource_name}: unexpected kind {app.get("kind")}')
                ingress = app['properties']['configuration']['ingress']
                assert ingress['external'] is True, f'{resource_name}: endpoint is not external'
                assert ingress['targetPort'] == target_port, (
                    f'{resource_name}: expected target port {target_port}, got {ingress["targetPort"]}')
                containers = app['properties']['template']['containers']
                assert len(containers) == 1, f'{resource_name}: expected one workload container'
                env = {item['name']: item for item in containers[0].get('env', [])}
                for name in required_service_uris:
                    value = env.get(name, {}).get('value', '')
                    assert value.startswith('https://'), (
                        f'{resource_name}: missing managed-identity service URI setting {name}')
                return app, ingress, containers[0], env

            node_app, node_ingress, node_container, _ = inspect_app(
                'node-functions',
                80,
                [
                    'AzureWebJobsStorage__blobServiceUri',
                    'AzureWebJobsStorage__queueServiceUri',
                    'blobs__blobServiceUri',
                    'workQueue__queueServiceUri'
                ])
            dotnet_app, dotnet_ingress, dotnet_container, dotnet_env = inspect_app(
                'dotnet-functions',
                8080,
                [
                    'AzureWebJobsStorage__blobServiceUri',
                    'AzureWebJobsStorage__queueServiceUri',
                    'AzureWebJobsStorage__tableServiceUri',
                    'AzureWebJobsStorage__dataLakeServiceUri',
                    'dotnetBlobs__blobServiceUri',
                    'dotnetWorkQueue__queueServiceUri'
                ])

            assert dotnet_env.get('AZURE_CLIENT_ID', {}).get('value'), (
                '.NET Functions: managed identity client ID is missing')
            assert dotnet_env.get('AZURE_TOKEN_CREDENTIALS', {}).get('value') == 'ManagedIdentityCredential', (
                '.NET Functions: managed identity credential selection is missing')
            assert (
                dotnet_env['AzureWebJobsStorage__blobServiceUri']['value']
                == dotnet_env['dotnetBlobs__blobServiceUri']['value']
            ), '.NET Functions: host and named blob bindings do not use the shared storage account'
            assert (
                dotnet_env['AzureWebJobsStorage__queueServiceUri']['value']
                == dotnet_env['dotnetWorkQueue__queueServiceUri']['value']
            ), '.NET Functions: host and named queue bindings do not use the shared storage account'
            for name in [
                'AzureWebJobsStorage',
                'dotnetBlobs',
                'dotnetWorkQueue',
                'ConnectionStrings__dotnetBlobs',
                'ConnectionStrings__dotnetWorkQueue'
            ]:
                assert name not in dotnet_env, (
                    f'.NET Functions: found connection-string setting instead of service URI: {name}')

            node_url = 'https://' + node_ingress['fqdn']
            dotnet_url = 'https://' + dotnet_ingress['fqdn']
            Path('node-functions-url.txt').write_text(node_url)
            Path('dotnet-functions-url.txt').write_text(dotnet_url)
            print(f'Node Functions URL: {node_url}', flush=True)
            print(f'.NET Functions URL: {dotnet_url}', flush=True)

            assert len(registries) == 1, (
                f'Expected one deployment registry, found {[registry["name"] for registry in registries]}')
            registry = registries[0]
            login_server = registry['loginServer']

            def verify_pushed_image(resource_name, container):
                image_reference = container['image']
                prefix = login_server + '/'
                assert image_reference.startswith(prefix), (
                    f'{resource_name}: image {image_reference} is not in deployment registry {login_server}')
                image = image_reference[len(prefix):]
                result = subprocess.run(
                    [
                        'az', 'acr', 'repository', 'show',
                        '--subscription', os.environ['AZURE__SUBSCRIPTIONID'],
                        '--name', registry['name'],
                        '--image', image,
                        '--only-show-errors',
                        '-o', 'json'
                    ],
                    capture_output=True,
                    text=True,
                    timeout=60)
                assert result.returncode == 0, (
                    f'{resource_name}: pushed image lookup failed for {image_reference}: '
                    f'{result.stderr or result.stdout}')
                metadata = json.loads(result.stdout)
                assert metadata.get('digest', '').startswith('sha256:'), (
                    f'{resource_name}: registry image has no digest: {metadata}')
                print(
                    f'{resource_name} image: {image_reference} ({metadata["digest"]})',
                    flush=True)

            verify_pushed_image('Node Functions', node_container)
            verify_pushed_image('.NET Functions SDK', dotnet_container)
            assert node_container['image'] != dotnet_container['image'], (
                'Node and .NET Functions unexpectedly reference the same image')

            def request_json_until_success(label, method, url, value, expected, timeout_seconds):
                deadline = time.monotonic() + timeout_seconds
                last_error = None
                while time.monotonic() < deadline:
                    try:
                        request = urllib.request.Request(
                            url,
                            method=method,
                            data=value.encode() if method == 'POST' else None,
                            headers={'Content-Type': 'text/plain'})
                        with urllib.request.urlopen(request, timeout=15) as response:
                            actual = json.load(response)
                        if actual == expected:
                            return
                        last_error = f'Unexpected response: {actual}'
                    except (urllib.error.URLError, TimeoutError, ValueError) as error:
                        last_error = str(error)
                    print(
                        f'{label} {method}: {last_error}; waiting for Functions startup / storage RBAC',
                        flush=True)
                    time.sleep(10)
                raise AssertionError(f'{label} {method} binding verification failed: {last_error}')

            def verify_node_roundtrip():
                request_id = uuid.uuid4().hex
                value = 'aspire-node-functions-binding-' + request_id
                url = node_url + '/api/roundtrip/' + request_id
                request_json_until_success(
                    'Node Functions',
                    'POST',
                    url,
                    value,
                    {'accepted': request_id},
                    timeout_seconds=480)
                request_json_until_success(
                    'Node Functions',
                    'GET',
                    url,
                    value,
                    {'direct': value, 'queued': value},
                    timeout_seconds=180)
                print('Verified Node HTTP, queue, and blob binding round trip.', flush=True)

            def verify_dotnet_roundtrip():
                request_id = uuid.uuid4().hex
                value = 'aspire-dotnet-functions-binding-' + request_id
                url = dotnet_url + '/api/roundtrip/' + value
                request_json_until_success(
                    '.NET Functions',
                    'POST',
                    url,
                    value,
                    {'accepted': value},
                    timeout_seconds=480)
                request_json_until_success(
                    '.NET Functions',
                    'GET',
                    url,
                    value,
                    {'queued': value},
                    timeout_seconds=240)
                print(
                    'Verified .NET HTTP -> queue output/trigger -> blob output/input round trip.',
                    flush=True)

            with ThreadPoolExecutor(max_workers=2) as executor:
                futures = [
                    executor.submit(verify_node_roundtrip),
                    executor.submit(verify_dotnet_roundtrip)
                ]
                for future in futures:
                    future.result()

            print(
                'Verified live Node and .NET Functions images, functionapp kinds, ingress ports, '
                'managed-identity storage settings, and binding round trips.',
                flush=True)
            """);
    }

    private static string? GetDeploymentDiagnostics(TemporaryWorkspace workspace)
    {
        var root = workspace.WorkspaceRoot.FullName;
        var diagnostics = new List<string>();
        var urls = new List<string>();
        foreach (var (name, fileName) in new[]
        {
            ("Node Functions", "node-functions-url.txt"),
            (".NET Functions", "dotnet-functions-url.txt")
        })
        {
            var path = Path.Combine(root, fileName);
            if (File.Exists(path))
            {
                urls.Add($"{name}: {File.ReadAllText(path).Trim()}");
            }
        }

        if (urls.Count > 0)
        {
            diagnostics.Add($"Discovered deployment URLs before failure:{Environment.NewLine}{string.Join(Environment.NewLine, urls)}");
        }

        var verificationLogPath = Path.Combine(root, "verify-functions.log");
        if (File.Exists(verificationLogPath))
        {
            const int maxLogLength = 16_000;
            var verificationLog = File.ReadAllText(verificationLogPath);
            if (verificationLog.Length > maxLogLength)
            {
                verificationLog = $"[earlier verification output omitted]{Environment.NewLine}{verificationLog[^maxLogLength..]}";
            }

            diagnostics.Add($"Live verification output:{Environment.NewLine}{verificationLog.Trim()}");
        }

        return diagnostics.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, diagnostics);
    }
}
