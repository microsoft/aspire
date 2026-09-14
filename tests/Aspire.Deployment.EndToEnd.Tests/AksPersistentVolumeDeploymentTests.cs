// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

[Trait("category", "deployment")]
[Trait("provider", "azure")]
public sealed class AksPersistentVolumeDeploymentTests(ITestOutputHelper output)
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(60);

    [Fact]
    public async Task DeployAksPersistentVolumeSurvivesRedeploy()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);

        await DeployAksPersistentVolumeSurvivesRedeployCore(linkedCts.Token);
    }

    private async Task DeployAksPersistentVolumeSurvivesRedeployCore(CancellationToken cancellationToken)
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
            else
            {
                Assert.Skip("Azure authentication not available. Run 'az login' to authenticate.");
            }
        }

        using var workspace = TemporaryWorkspace.Create(output);
        var startTime = DateTime.UtcNow;
        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("akspv");
        var projectName = "AksPersistentVolume";
        var deploymentUrls = new Dictionary<string, string>();

        // Cleanup is deliberately redundant with Aspire destroy. If the test fails during deployment,
        // deleting the resource group still removes the AKS cluster and its managed disk.
        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            await auto.PrepareEnvironmentAsync(workspace, counter);
            await auto.InstallCurrentBuildAspireCliAsync(counter, output);

            await auto.AspireNewAsync(projectName, counter, useRedisCache: false);

            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire add Aspire.Hosting.Azure.Kubernetes");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            var projectDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDirectory = Path.Combine(projectDirectory, $"{projectName}.AppHost");
            var appHostPath = Path.Combine(appHostDirectory, "AppHost.cs");
            var apiProgramPath = Path.Combine(
                projectDirectory,
                $"{projectName}.ApiService",
                "Program.cs");

            ConfigureAppHost(appHostPath);
            ConfigureApi(apiProgramPath);

            await auto.RunCommandAsync(
                $"dotnet build {projectName}.AppHost/{projectName}.AppHost.csproj --nologo",
                counter,
                TimeSpan.FromMinutes(5));

            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Linux environment variables are case-sensitive. Remove the job-level mixed-case value
            // before setting the value consumed by the deployment pipeline.
            await auto.TypeAsync(
                $"unset ASPIRE_PLAYGROUND && unset Azure__Location && " +
                $"export AZURE__LOCATION=westus3 && export AZURE__RESOURCEGROUP={resourceGroupName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await DeployAsync(auto, counter, waitForPipelineSuccess: true);

            await auto.RunCommandAsync(
                $"AKS_NAME=$(az aks list --resource-group {resourceGroupName} --query '[0].name' --output tsv) && " +
                "test -n \"$AKS_NAME\" && echo \"AKS cluster: $AKS_NAME\"",
                counter,
                TimeSpan.FromMinutes(2));
            await auto.RunCommandAsync(
                $"az aks get-credentials --resource-group {resourceGroupName} --name \"$AKS_NAME\" --overwrite-existing",
                counter,
                TimeSpan.FromMinutes(2));
            await auto.RunCommandAsync(
                "NS=$(kubectl get service --all-namespaces " +
                "-o jsonpath='{range .items[?(@.metadata.name==\"apiservice-service\")]}{.metadata.namespace}{end}') && " +
                "test -n \"$NS\" && echo \"Kubernetes namespace: $NS\"",
                counter,
                TimeSpan.FromMinutes(2));

            await WaitForStatefulSetAndManagedDiskAsync(auto, counter);

            await VerifyFileSystemGroupAsync(auto, counter, expectedFsGroup: 2000);

            await auto.RunCommandAsync(
                "PVC_UID_BEFORE=$(kubectl get persistentvolumeclaim data --namespace \"$NS\" -o jsonpath='{.metadata.uid}') && " +
                "POD_UID_BEFORE=$(kubectl get pod apiservice-statefulset-0 --namespace \"$NS\" -o jsonpath='{.metadata.uid}') && " +
                "test -n \"$PVC_UID_BEFORE\" && test -n \"$POD_UID_BEFORE\" && " +
                "echo \"First PVC UID: $PVC_UID_BEFORE\" && echo \"First pod UID: $POD_UID_BEFORE\"",
                counter);

            var apiPort = GetAvailablePort();
            await StartPortForwardAsync(auto, counter, apiPort);
            await VerifyApiResponseAsync(
                auto,
                counter,
                apiPort,
                "?action=write",
                "PASSED: wrote aks-pv-marker-42 revision first");
            await StopPortForwardAsync(auto, counter);

            UpdateDeploymentRevision(appHostPath);

            await DeployAsync(auto, counter, waitForPipelineSuccess: false);

            await auto.RunCommandAsync(
                "kubectl rollout status statefulset/apiservice-statefulset --namespace \"$NS\" --timeout=10m",
                counter,
                TimeSpan.FromMinutes(11));
            await auto.RunCommandAsync(
                "kubectl wait --for=condition=Ready pod/apiservice-statefulset-0 --namespace \"$NS\" --timeout=5m",
                counter,
                TimeSpan.FromMinutes(6));
            await VerifyFileSystemGroupAsync(auto, counter, expectedFsGroup: 3000);
            await auto.RunCommandAsync(
                "PVC_UID_AFTER=$(kubectl get persistentvolumeclaim data --namespace \"$NS\" -o jsonpath='{.metadata.uid}') && " +
                "POD_UID_AFTER=$(kubectl get pod apiservice-statefulset-0 --namespace \"$NS\" -o jsonpath='{.metadata.uid}') && " +
                "test \"$PVC_UID_AFTER\" = \"$PVC_UID_BEFORE\" && " +
                "test \"$POD_UID_AFTER\" != \"$POD_UID_BEFORE\" && " +
                "echo \"Redeploy reused PVC $PVC_UID_AFTER and replaced pod $POD_UID_BEFORE with $POD_UID_AFTER\"",
                counter);

            apiPort = GetAvailablePort();
            await StartPortForwardAsync(auto, counter, apiPort);
            await VerifyApiResponseAsync(
                auto,
                counter,
                apiPort,
                "?action=read",
                "PASSED: read aks-pv-marker-42 revision second");
            await VerifyApiResponseAsync(
                auto,
                counter,
                apiPort,
                "?action=write-new",
                "PASSED: wrote new aks-pv-marker-42 revision second");
            await StopPortForwardAsync(auto, counter);

            await auto.AspireDestroyAsync(counter);

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployAksPersistentVolumeSurvivesRedeploy),
                resourceGroupName,
                deploymentUrls,
                duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Test failed after {duration}: {ex.Message}");
            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployAksPersistentVolumeSurvivesRedeploy),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);
            throw;
        }
        finally
        {
            await CleanupResourceGroupAsync(resourceGroupName);
        }
    }

    private static void ConfigureAppHost(string appHostPath)
    {
        var content = File.ReadAllText(appHostPath);

        content = ReplaceRequired(
            content,
            "var builder = DistributedApplication.CreateBuilder(args);",
            """
            #pragma warning disable ASPIREAZURE003
            #pragma warning disable ASPIRECOMPUTE002

            var builder = DistributedApplication.CreateBuilder(args);

            // Pin both pools to the VM family provisioned by the deployment test subscription.
            // Without the explicit workload pool, AKS creates it with the Standard_D2s_v5 default.
            var aks = builder.AddAzureKubernetesEnvironment("aks")
                .WithSystemNodePool("Standard_D2as_v5");
            aks.AddNodePool("workload", "Standard_D2as_v5", 1, 3);

            // Omitting WithStorageClass exercises the standard AKS default StorageClass,
            // which dynamically provisions an Azure Managed Disk.
            var data = aks.AddPersistentVolume("data")
                .WithCapacity("1Gi");
            """,
            appHostPath);

        content = ReplaceRequired(
            content,
            """builder.AddProject<Projects.AksPersistentVolume_ApiService>("apiservice")""",
            """
            builder.AddProject<Projects.AksPersistentVolume_ApiService>("apiservice")
                .WithPersistentVolume(data, "/srv/data", env: "DATA_PATH")
                .WithEnvironment("DEPLOYMENT_REVISION", "first")
            """,
            appHostPath);

        File.WriteAllText(appHostPath, content);
    }

    private static void UpdateDeploymentRevision(string appHostPath)
    {
        var content = File.ReadAllText(appHostPath);
        content = ReplaceRequired(
            content,
            """.WithEnvironment("DEPLOYMENT_REVISION", "first")""",
            """
            .WithEnvironment("DEPLOYMENT_REVISION", "second")
                .PublishAsKubernetesService(resource =>
                {
                    var podSpec = resource.Workload?.PodTemplate.Spec
                        ?? throw new InvalidOperationException("The API Kubernetes workload was not generated.");
                    podSpec.SecurityContext ??= new();
                    podSpec.SecurityContext.FsGroup = 3000;
                })
            """,
            appHostPath);
        File.WriteAllText(appHostPath, content);
    }

    private static void ConfigureApi(string apiProgramPath)
    {
        File.WriteAllText(
            apiProgramPath,
            """
            var builder = WebApplication.CreateBuilder(args);

            builder.AddServiceDefaults();

            var app = builder.Build();

            var dataPath = app.Configuration["DATA_PATH"]
                ?? throw new InvalidOperationException("DATA_PATH is not configured.");
            var markerPath = Path.Combine(dataPath, "marker.txt");
            var newMarkerPath = Path.Combine(dataPath, "new-marker.txt");
            const string markerToken = "aks-pv-marker-42";
            var deploymentRevision = app.Configuration["DEPLOYMENT_REVISION"]
                ?? throw new InvalidOperationException("DEPLOYMENT_REVISION is not configured.");

            app.MapGet("/", async (string action) =>
            {
                if (action == "write")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
                    await File.WriteAllTextAsync(markerPath, markerToken);
                    return Results.Ok($"PASSED: wrote {markerToken} revision {deploymentRevision}");
                }

                if (action == "read")
                {
                    if (!File.Exists(markerPath))
                    {
                        return Results.NotFound("FAILED: marker file was not found");
                    }

                    var persistedValue = await File.ReadAllTextAsync(markerPath);
                    if (persistedValue != markerToken)
                    {
                        return Results.Problem($"FAILED: expected {markerToken}, got {persistedValue}");
                    }

                    return Results.Ok($"PASSED: read {persistedValue} revision {deploymentRevision}");
                }

                if (action == "write-new")
                {
                    await File.WriteAllTextAsync(newMarkerPath, markerToken);
                    return Results.Ok($"PASSED: wrote new {markerToken} revision {deploymentRevision}");
                }

                return Results.BadRequest("FAILED: action must be write, read, or write-new");
            });

            app.MapDefaultEndpoints();
            app.Run();
            """);
    }

    private static async Task WaitForStatefulSetAndManagedDiskAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync(
            "kubectl get statefulset apiservice-statefulset --namespace \"$NS\"",
            counter,
            TimeSpan.FromMinutes(2));
        await auto.RunCommandAsync(
            "phase=''; for i in $(seq 1 60); do " +
            "phase=$(kubectl get persistentvolumeclaim data --namespace \"$NS\" -o jsonpath='{.status.phase}' 2>/dev/null || true); " +
            "if [ \"$phase\" = \"Bound\" ]; then break; fi; sleep 5; done; " +
            "test \"$phase\" = \"Bound\" && " +
            "STORAGE_CLASS=$(kubectl get persistentvolumeclaim data --namespace \"$NS\" -o jsonpath='{.spec.storageClassName}') && " +
            "PROVISIONER=$(kubectl get storageclass \"$STORAGE_CLASS\" -o jsonpath='{.provisioner}') && " +
            "test \"$PROVISIONER\" = \"disk.csi.azure.com\" && " +
            "echo \"PVC data is Bound using $STORAGE_CLASS ($PROVISIONER)\"",
            counter,
            TimeSpan.FromMinutes(6));
        await auto.RunCommandAsync(
            "kubectl wait --for=condition=Ready pod/apiservice-statefulset-0 --namespace \"$NS\" --timeout=5m",
            counter,
            TimeSpan.FromMinutes(6));
    }

    private static async Task VerifyFileSystemGroupAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        long expectedFsGroup)
    {
        await auto.RunCommandAsync(
            $"FS_GROUP=$(kubectl get statefulset apiservice-statefulset --namespace \"$NS\" -o jsonpath='{{.spec.template.spec.securityContext.fsGroup}}') && " +
            $"test \"$FS_GROUP\" = \"{expectedFsGroup}\" && " +
            // Verify the Linux identity and mount ownership reported as:
            //   id -u: 1654
            //   id -G: 1654 2000
            //   stat -c %g /srv/data: 2000
            // This proves the write succeeds through group access rather than root privileges.
            "PROCESS_UID=$(kubectl exec pod/apiservice-statefulset-0 --namespace \"$NS\" -- id -u) && " +
            "PROCESS_GROUPS=$(kubectl exec pod/apiservice-statefulset-0 --namespace \"$NS\" -- id -G) && " +
            "VOLUME_GROUP=$(kubectl exec pod/apiservice-statefulset-0 --namespace \"$NS\" -- stat -c %g /srv/data) && " +
            "test \"$PROCESS_UID\" != \"0\" && " +
            $"printf ' %s ' \"$PROCESS_GROUPS\" | grep --fixed-strings --quiet ' {expectedFsGroup} ' && " +
            $"test \"$VOLUME_GROUP\" = \"{expectedFsGroup}\" && " +
            $"echo \"StatefulSet uses fsGroup {expectedFsGroup}; pod UID is $PROCESS_UID with groups $PROCESS_GROUPS; /srv/data group is $VOLUME_GROUP\"",
            counter);
    }

    private static async Task DeployAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        bool waitForPipelineSuccess)
    {
        await auto.TypeAsync("aspire deploy --clear-cache");
        await auto.EnterAsync();

        if (waitForPipelineSuccess)
        {
            await auto.WaitForPipelineSuccessAsync(timeout: TimeSpan.FromMinutes(30));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));
        }
        else
        {
            // The first deploy's "Pipeline succeeded" marker can still be in the terminal viewport.
            // The sequence-numbered prompt belongs only to this redeploy and therefore cannot match
            // stale output from the first deployment.
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(30));
        }
    }

    private static async Task StartPortForwardAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        int port)
    {
        await auto.RunCommandAsync(
            $"kubectl port-forward service/apiservice-service {port}:8080 --namespace \"$NS\" >/tmp/aks-pv-port-forward.log 2>&1 & PORT_FORWARD_PID=$!; " +
            "test -n \"$PORT_FORWARD_PID\"",
            counter);
    }

    private static async Task StopPortForwardAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter)
    {
        await auto.RunCommandAsync(
            "kill \"$PORT_FORWARD_PID\" 2>/dev/null || true; wait \"$PORT_FORWARD_PID\" 2>/dev/null || true; unset PORT_FORWARD_PID",
            counter);
    }

    private static async Task VerifyApiResponseAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        int port,
        string query,
        string expectedResponse)
    {
        await auto.RunCommandAsync(
            $"verified=0; for i in $(seq 1 60); do " +
            $"response=$(curl --silent --fail 'http://localhost:{port}/{query}' 2>/dev/null || true); " +
            $"if printf '%s' \"$response\" | grep --fixed-strings --quiet '{expectedResponse}'; then " +
            "echo \"API response: $response\"; verified=1; break; fi; sleep 2; done; " +
            "if [ \"$verified\" != \"1\" ]; then " +
            "echo 'Port-forward log:'; cat /tmp/aks-pv-port-forward.log 2>/dev/null || true; " +
            "echo 'API pod log:'; kubectl logs pod/apiservice-statefulset-0 --namespace \"$NS\" --tail=100 || true; " +
            "fi; test \"$verified\" = \"1\"",
            counter,
            TimeSpan.FromMinutes(3));
    }

    private static string ReplaceRequired(
        string content,
        string oldValue,
        string newValue,
        string filePath)
    {
        if (!content.Contains(oldValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected content was not found while updating '{filePath}'.");
        }

        return content.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task CleanupResourceGroupAsync(string resourceGroupName)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "az",
                Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                output.WriteLine($"Resource group deletion initiated: {resourceGroupName}");
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: true, "Deletion initiated");
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync();
                output.WriteLine($"Resource group deletion may have failed (exit code {process.ExitCode}): {error}");
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, $"Exit code {process.ExitCode}: {error}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed to cleanup resource group: {ex.Message}");
            DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, ex.Message);
        }
    }
}
