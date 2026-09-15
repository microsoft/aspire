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
    public async Task DeployNodeFunctionsWithStorageBindingsToAzureContainerApps()
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

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, TestContext.Current.CancellationToken);
        using var workspace = TemporaryWorkspace.Create(output);
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("node-functions");
        var started = Stopwatch.StartNew();

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(linkedCts.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireBundleAsync(counter, output);
            await auto.RunCommandAsync("aspire init --language typescript --non-interactive", counter, TimeSpan.FromMinutes(2));

            foreach (var package in new[] { "Aspire.Hosting.Azure.AppContainers", "Aspire.Hosting.Azure.Functions", "Aspire.Hosting.Azure.Storage" })
            {
                await auto.TypeAsync($"aspire add {package}");
                await auto.EnterAsync();
                await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(3));
            }

            WriteFunctionsApp(workspace);
            await auto.RunCommandAsync($"unset ASPIRE_PLAYGROUND && export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}", counter);
            await auto.TypeAsync("aspire deploy --clear-cache");
            await auto.EnterAsync();
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Inspect the actual deployed resource, not just the generated Bicep. The HTTP
            // round trip below also proves the generated image listens on its declared port.
            await auto.RunCommandAsync(
                $"az containerapp list -g {resourceGroupName} -o json > containerapps.json && python3 verify-functions.py",
                counter, TimeSpan.FromMinutes(12));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployNodeFunctionsWithStorageBindingsToAzureContainerApps),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["Functions"] = File.ReadAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "functions-url.txt"))
                },
                started.Elapsed);
        }
        catch (Exception ex)
        {
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployNodeFunctionsWithStorageBindingsToAzureContainerApps),
                resourceGroupName, ex.Message, ex.StackTrace);
            throw;
        }
        finally
        {
            // Cleanup has its own budget so a deployment timeout cannot prevent deletion.
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("az")
                {
                    ArgumentList = { "group", "delete", "--name", resourceGroupName, "--yes", "--no-wait" },
                    UseShellExecute = false
                }
            };
            try
            {
                process.Start();
                await process.WaitForExitAsync(cleanupTimeout.Token);
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, process.ExitCode == 0,
                    process.ExitCode == 0 ? "Cleanup requested" : $"az group delete exited with {process.ExitCode}");
            }
            catch (Exception ex)
            {
                output.WriteLine($"Cleanup failed: {ex.Message}");
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, errorMessage: ex.Message);
            }
        }
    }

    private static void WriteFunctionsApp(TemporaryWorkspace workspace)
    {
        var root = workspace.WorkspaceRoot.FullName;
        var functions = Directory.CreateDirectory(Path.Combine(root, "functions")).FullName;

        // Deliberately omit a Dockerfile and built output: deployment must generate the
        // Node Functions image, install dependencies, and compile the TypeScript fixture.
        File.WriteAllText(Path.Combine(functions, "package.json"), """
            {
              "name": "functions-storage-deployment-test",
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

            const blobOutput = output.storageBlob({ path: 'messages/{id}.txt', connection: 'blobs' });
            const queueOutput = output.storageQueue({ queueName: 'work', connection: 'workQueue' });
            const receiptOutput = output.storageBlob({ path: 'receipts/{id}.txt', connection: 'blobs' });
            const blobInput = input.storageBlob({ path: 'messages/{id}.txt', connection: 'blobs' });
            const receiptInput = input.storageBlob({ path: 'receipts/{id}.txt', connection: 'blobs' });

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
                queueName: 'work', connection: 'workQueue',
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
        File.WriteAllText(Path.Combine(root, "apphost.mts"), """
            import { AzureFunctionsLanguage, createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();
            await builder.addAzureContainerAppEnvironment('env');
            const storage = await builder.addAzureStorage('storage');
            const blobs = await storage.addBlobs('blobs');
            await storage.addBlobContainer('messages');
            await storage.addBlobContainer('receipts');
            const queues = await storage.addQueues('queues');
            await storage.addQueue('work');

            await builder.addAzureFunctionsApp('functions', './functions', AzureFunctionsLanguage.TypeScript)
                .withExternalHttpEndpoints()
                .withReference(blobs)
                .withReference(queues, { connectionName: 'workQueue' });

            await builder.build().run();
            """);
        File.WriteAllText(Path.Combine(root, "verify-functions.py"), """
            import json
            import time
            import urllib.error
            import urllib.request
            import uuid
            from pathlib import Path

            # ARM container app JSON has top-level kind and properties.configuration.ingress.
            # Select by the Aspire resource name, rather than accepting the dashboard endpoint.
            apps = json.loads(Path('containerapps.json').read_text())
            functions = [app for app in apps if 'functions' in app['name']]
            assert len(functions) == 1, f'Expected one Functions container app, found {len(functions)}'
            app = functions[0]
            assert app.get('kind', '').lower() == 'functionapp', f"Unexpected kind: {app.get('kind')}"
            ingress = app['properties']['configuration']['ingress']
            assert ingress['external'] is True, 'Functions endpoint is not external'
            assert ingress['targetPort'] == 80, f"Unexpected target port: {ingress['targetPort']}"
            env = {
                item['name']: item
                for container in app['properties']['template']['containers']
                for item in container.get('env', [])
            }
            for name in ['AzureWebJobsStorage__blobServiceUri', 'blobs__blobServiceUri', 'workQueue__queueServiceUri']:
                assert env.get(name, {}).get('value', '').startswith('https://'), f'Missing identity-based binding: {name}'

            base_url = 'https://' + ingress['fqdn']
            Path('functions-url.txt').write_text(base_url)
            request_id = uuid.uuid4().hex
            value = 'aspire-functions-binding-' + request_id
            url = base_url + '/api/roundtrip/' + request_id

            def request_until_success(method, expected, timeout_seconds):
                deadline = time.monotonic() + timeout_seconds
                last_error = None
                while time.monotonic() < deadline:
                    try:
                        request = urllib.request.Request(
                            url, method=method,
                            data=value.encode() if method == 'POST' else None,
                            headers={'Content-Type': 'text/plain'})
                        with urllib.request.urlopen(request, timeout=10) as response:
                            actual = json.load(response)
                        if actual == expected:
                            return
                        last_error = f'Unexpected response: {actual}'
                    except (urllib.error.URLError, TimeoutError, ValueError) as error:
                        last_error = str(error)
                    print(f'{method}: {last_error}; waiting for Functions startup / storage RBAC', flush=True)
                    time.sleep(10)
                raise AssertionError(f'{method} binding verification failed: {last_error}')

            # Separate startup/RBAC and queue-processing budgets so a slow startup cannot
            # consume the GET retries. Eleven minutes leaves room in the 12-minute command
            # timeout for az and the final in-flight request/retry delay of each phase.
            request_until_success('POST', {'accepted': request_id}, timeout_seconds=480)
            # Both values must be read back through real Functions blob input bindings.
            # The second blob can only exist after queue output + trigger + blob output succeed.
            request_until_success('GET', {'direct': value, 'queued': value}, timeout_seconds=180)
            print('Verified generated Node image, functionapp kind, port 80, and storage binding round trip.')
            """);
    }
}
