// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Deployment.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Deployment.EndToEnd.Tests;

/// <summary>
/// End-to-end test that deploys the Aspire starter template to a live Radius environment
/// running on Azure Kubernetes Service (AKS).
/// </summary>
/// <remarks>
/// Unlike the Radius unit/snapshot tests (which only prove the Bicep serializer output), this
/// test exercises the full <c>aspire publish</c> → <c>rad deploy app.bicep</c> path against a
/// real cluster and verifies the deployed application actually runs. It provisions an AKS
/// cluster + ACR, installs the Radius control plane onto the cluster, deploys the starter app,
/// and asserts the workloads become ready and serve HTTP traffic.
///
/// The Radius publisher does not build or push container images for project resources yet
/// (tracked at https://github.com/microsoft/aspire/issues/16844), so the test builds and pushes
/// the starter's images to ACR itself and attaches the resulting references with
/// <c>WithContainerImage</c> before publishing.
/// </remarks>
public sealed class RadiusStarterDeploymentTests(ITestOutputHelper output)
{
    // AKS provisioning (~10-15 min) + Radius control-plane install + two image builds + recipe
    // deployment push the total well past the pure-AKS test's budget, so allow up to 55 minutes.
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromMinutes(55);

    [Fact]
    public async Task DeployStarterTemplateToRadiusOnAks()
    {
        using var cts = new CancellationTokenSource(s_testTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token, TestContext.Current.CancellationToken);
        var cancellationToken = linkedCts.Token;

        await DeployStarterTemplateToRadiusOnAksCore(cancellationToken);
    }

    private async Task DeployStarterTemplateToRadiusOnAksCore(CancellationToken cancellationToken)
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

        var resourceGroupName = DeploymentE2ETestHelpers.GenerateResourceGroupName("radius");
        var clusterName = $"radius-{DeploymentE2ETestHelpers.GetRunId()}-{DeploymentE2ETestHelpers.GetRunAttempt()}";
        // ACR names must be alphanumeric only, 5-50 chars, globally unique.
        var acrName = $"acrrad{DeploymentE2ETestHelpers.GetRunId()}{DeploymentE2ETestHelpers.GetRunAttempt()}".ToLowerInvariant();
        acrName = new string(acrName.Where(char.IsLetterOrDigit).Take(50).ToArray());
        if (acrName.Length < 5)
        {
            acrName = $"acrrad{Guid.NewGuid():N}"[..24];
        }

        var acrLoginServer = $"{acrName}.azurecr.io";
        var apiServiceImage = $"{acrLoginServer}/apiservice:latest";
        var webFrontendImage = $"{acrLoginServer}/webfrontend:latest";

        const string projectName = "RadiusStarter";
        // Use a unique namespace so reruns / parallel jobs never collide in the shared "default"
        // namespace and so label-based verification below is unambiguous.
        var appNamespace = $"radius-{DeploymentE2ETestHelpers.GetRunId()}-{DeploymentE2ETestHelpers.GetRunAttempt()}".ToLowerInvariant();

        output.WriteLine($"Test: {nameof(DeployStarterTemplateToRadiusOnAks)}");
        output.WriteLine($"Resource Group: {resourceGroupName}");
        output.WriteLine($"AKS Cluster: {clusterName}");
        output.WriteLine($"ACR Name: {acrName}");
        output.WriteLine($"App namespace: {appNamespace}");
        output.WriteLine($"Subscription: {subscriptionId[..8]}...");
        output.WriteLine($"Workspace: {workspace.WorkspaceRoot.FullName}");

        try
        {
            using var terminal = DeploymentE2ETestHelpers.CreateTestTerminal();
            var pendingRun = terminal.RunAsync(cancellationToken);

            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

            // ===== PHASE 1: Provision AKS + ACR =====

            output.WriteLine("Step 1: Preparing environment...");
            await auto.PrepareEnvironmentAsync(workspace, counter);

            // Step 1b: Isolate kubeconfig and the Radius (`rad`) config to throwaway files under the
            // TemporaryWorkspace so this test never mutates the developer's real ~/.kube/config or
            // ~/.rad/config.yaml (test-isolation requirement, .github/instructions/test-review-guidelines).
            //
            // - KUBECONFIG: az/kubectl/rad all honor it, so `az aks get-credentials` writes here and
            //   every kube-touching command uses this file. Removed with the workspace on dispose.
            // - rad config: `rad` only accepts a `--config` flag (no config env var), and `aspire deploy`
            //   spawns `rad deploy` internally (FileName="rad", UseShellExecute=false -> PATH lookup;
            //   see src/Aspire.Hosting.Radius/Publishing/RadiusDeploymentPipelineStep.cs). So we shadow
            //   `rad` with a workspace-local shim earlier on PATH that injects
            //   `--config <ws>/.rad/config.yaml` into every invocation (global flag, valid before any
            //   subcommand). REAL_RAD is resolved BEFORE prepending the shim dir, so the shim never
            //   recurses. This isolates the config file and its lock without backing up/restoring the
            //   user's real config (which would race with any concurrent `rad` for the ~55-min run).
            var wsRoot = workspace.WorkspaceRoot.FullName;
            output.WriteLine("Step 1b: Isolating kubeconfig and rad config to the workspace...");
            await auto.TypeAsync(
                $"REAL_RAD=\"$(command -v rad)\" && " +
                $"mkdir -p \"{wsRoot}/bin\" \"{wsRoot}/.rad\" \"{wsRoot}/.kube\" && " +
                $"printf '#!/usr/bin/env bash\\nexec \"%s\" --config \"%s\" \"$@\"\\n' \"$REAL_RAD\" \"{wsRoot}/.rad/config.yaml\" > \"{wsRoot}/bin/rad\" && " +
                $"chmod +x \"{wsRoot}/bin/rad\" && " +
                $"export PATH=\"{wsRoot}/bin:$PATH\" && " +
                $"export KUBECONFIG=\"{wsRoot}/.kube/config\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 2: Registering required resource providers...");
            await auto.TypeAsync("az provider register --namespace Microsoft.ContainerService --wait && " +
                  "az provider register --namespace Microsoft.ContainerRegistry --wait");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            output.WriteLine("Step 3: Creating resource group...");
            await auto.TypeAsync($"az group create --name {resourceGroupName} --location westus3 --output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 4: Creating Azure Container Registry...");
            await auto.TypeAsync($"az acr create --resource-group {resourceGroupName} --name {acrName} --sku Basic --output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // Log into ACR immediately (before AKS creation which takes 10-15 min). The OIDC
            // federated token expires after ~5 minutes, so authenticate while it's fresh. Docker
            // credentials persist in ~/.docker/config.json.
            output.WriteLine("Step 4b: Logging into Azure Container Registry (early, before token expires)...");
            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 5: Creating AKS cluster (this may take 10-15 minutes)...");
            await auto.TypeAsync($"az aks create " +
                  $"--resource-group {resourceGroupName} " +
                  $"--name {clusterName} " +
                  $"--node-count 1 " +
                  $"--node-vm-size Standard_D2s_v3 " +
                  $"--generate-ssh-keys " +
                  $"--attach-acr {acrName} " +
                  $"--enable-managed-identity " +
                  $"--output table");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(20));

            output.WriteLine("Step 6: Verifying AKS-ACR integration...");
            await auto.TypeAsync($"az aks update --resource-group {resourceGroupName} --name {clusterName} --attach-acr {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            output.WriteLine("Step 7: Configuring kubectl credentials...");
            // Writes to the isolated $KUBECONFIG (Step 1b), not the developer's ~/.kube/config.
            await auto.TypeAsync($"az aks get-credentials --resource-group {resourceGroupName} --name {clusterName} --overwrite-existing");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 8: Verifying kubectl connectivity...");
            await auto.TypeAsync("kubectl get nodes");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // ===== PHASE 2: Install the Radius control plane on the cluster =====

            // Install the Radius control plane. The `rad` CLI version is pinned by the
            // "Setup Radius" workflow step, and `rad install kubernetes` installs the matching
            // control plane onto the current kube context.
            output.WriteLine("Step 9: Installing the Radius control plane onto the cluster...");
            await auto.TypeAsync("rad install kubernetes");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(10));

            // Configure a deterministic default workspace bound to this cluster. `rad install
            // kubernetes` already ensures the `default` group and environment exist; `rad deploy`
            // (invoked by `aspire deploy`) passes no --workspace/--group/--environment, so it
            // resolves the default workspace scope. Create the workspace explicitly instead of
            // relying on the interactive `rad init`. This (and every other `rad`) goes through the
            // Step 1b shim, so it writes the workspace-local config and sets itself current there.
            // No `--force`: the isolated config starts empty, so the name can never collide with a
            // pre-existing workspace (and there is no user workspace to clobber).
            output.WriteLine("Step 10: Creating Radius workspace (default scope)...");
            await auto.TypeAsync($"rad workspace create kubernetes radius-e2e --context $(kubectl config current-context) --group default --environment default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            output.WriteLine("Step 11: Verifying Radius environment...");
            await auto.TypeAsync("rad version && rad env show default --group default");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // ===== PHASE 3: Create the Aspire starter project and target Radius =====

            await auto.InstallCurrentBuildAspireCliAsync(counter, output, "Step 12");

            // Redis is intentionally enabled: it exercises the Radius container recipe and the
            // connection-string wiring from webfrontend to a Radius-deployed cache, which is part
            // of the realistic starter scenario. (Redis is a ContainerResource with its own image,
            // so it is not the subject of the WithContainerImage project-image workaround.)
            output.WriteLine("Step 13: Creating Aspire starter project (with Redis cache)...");
            await auto.AspireNewAsync(projectName, counter, useRedisCache: true);

            output.WriteLine("Step 14: Creating application namespace...");
            await auto.TypeAsync($"kubectl create namespace {appNamespace} --dry-run=client -o yaml | kubectl apply -f -");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 15: Adding the Aspire.Hosting.Radius package...");
            await auto.TypeAsync($"cd {projectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire add Aspire.Hosting.Radius");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter);

            // Edit AppHost.cs: target the Radius environment and attach the ACR image references
            // the publisher requires for project resources.
            var projectDir = Path.Combine(workspace.WorkspaceRoot.FullName, projectName);
            var appHostDir = Path.Combine(projectDir, $"{projectName}.AppHost");
            var appHostFilePath = Path.Combine(appHostDir, "AppHost.cs");

            output.WriteLine($"Step 16: Modifying AppHost.cs at: {appHostFilePath}");
            var content = File.ReadAllText(appHostFilePath);

            // Attach the pushed ACR image to each project resource. The Radius publisher fails
            // publish for project resources without an image annotation (no <name>:latest
            // fallback) - see WithContainerImage / issue 16844. Inserting WithContainerImage
            // immediately after the resource name keeps the existing fluent chain valid.
            content = content.Replace(
                "(\"apiservice\")",
                $"(\"apiservice\")\n    .WithContainerImage(\"{apiServiceImage}\")");
            content = content.Replace(
                "(\"webfrontend\")",
                $"(\"webfrontend\")\n    .WithContainerImage(\"{webFrontendImage}\")");

            // Register the Radius compute environment before Build().
            content = content.Replace(
                "builder.Build().Run();",
                $"builder.AddRadiusEnvironment(\"radius\").WithNamespace(\"{appNamespace}\");\n\nbuilder.Build().Run();");

            // WithContainerImage is Experimental (ASPIRERADIUS057) and the pipeline APIs used by
            // compute environments are Experimental (ASPIREPIPELINES001); suppress both. Add each
            // pragma independently so one already being present (e.g. a future template that emits
            // ASPIREPIPELINES001 itself) never skips adding the other and fails the warning-as-error build.
            if (!content.Contains("#pragma warning disable ASPIREPIPELINES001"))
            {
                content = "#pragma warning disable ASPIREPIPELINES001\n" + content;
            }
            if (!content.Contains("#pragma warning disable ASPIRERADIUS057"))
            {
                content = "#pragma warning disable ASPIRERADIUS057\n" + content;
            }

            File.WriteAllText(appHostFilePath, content);
            output.WriteLine("Modified AppHost.cs with AddRadiusEnvironment + WithContainerImage");

            // ===== PHASE 4: Build and push the container images to ACR =====

            // Re-login to ACR: the initial login (Step 4b) may have expired during the 10-15 min
            // AKS provisioning because OIDC federated tokens have a short (~5 min) lifetime.
            output.WriteLine("Step 17: Refreshing ACR login...");
            await auto.TypeAsync($"az acr login --name {acrName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 18: Building and pushing container images to ACR...");
            await auto.TypeAsync($"dotnet publish {projectName}.Web/{projectName}.Web.csproj " +
                  $"/t:PublishContainer " +
                  $"/p:ContainerRegistry={acrLoginServer} " +
                  $"/p:ContainerImageName=webfrontend " +
                  $"/p:ContainerImageTag=latest");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            await auto.TypeAsync($"dotnet publish {projectName}.ApiService/{projectName}.ApiService.csproj " +
                  $"/t:PublishContainer " +
                  $"/p:ContainerRegistry={acrLoginServer} " +
                  $"/p:ContainerImageName=apiservice " +
                  $"/p:ContainerImageTag=latest");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // ===== PHASE 5: Publish, verify Bicep, then deploy via rad =====

            output.WriteLine("Step 19: Navigating to AppHost directory...");
            await auto.TypeAsync($"cd {projectName}.AppHost");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Publish first to inspect the generated app.bicep. Fail fast if the ACR image
            // references did not make it into the Bicep (the most likely wiring regression).
            output.WriteLine("Step 20: Running aspire publish and verifying app.bicep image references...");
            await auto.TypeAsync("aspire publish --output-path ../out");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Split the marker token in the source command (BICEP_IMAGES''_OK evaluates to
            // BICEP_IMAGES_OK) so the searched string appears only in the command's *output* when
            // both greps match, not in the echoed command line itself. The grep -q chain's exit
            // code is still the hard gate (a failed grep trips the ERR prompt below); this marker
            // makes a successful match an explicit positive signal in the recording.
            await auto.TypeAsync($"grep -q '{acrLoginServer}/apiservice' ../out/app.bicep && " +
                  $"grep -q '{acrLoginServer}/webfrontend' ../out/app.bicep && echo BICEP_IMAGES''_OK");
            await auto.EnterAsync();
            await auto.WaitUntilAsync(
                s => new CellPatternSearcher().Find("BICEP_IMAGES_OK").Search(s).Count > 0,
                timeout: TimeSpan.FromSeconds(30),
                description: "app.bicep contains ACR image references");
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            output.WriteLine("Step 21: Verifying AKS can pull from ACR before deploying...");
            await auto.TypeAsync($"az aks check-acr --resource-group {resourceGroupName} --name {clusterName} --acr {acrLoginServer}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            output.WriteLine("Step 22: Deploying to Radius via aspire deploy (rad deploy app.bicep)...");
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            // Wait on this command's sequence-numbered prompt (not WaitForPipelineSuccessAsync).
            // WaitForPipelineSuccessAsync scans the whole viewport for "Pipeline succeeded", but the
            // earlier `aspire publish` (Step 20) already printed that exact marker and it is still
            // on screen, so the pipeline wait could match the stale publish marker and return
            // immediately -- collapsing the real deploy budget down to the following prompt wait.
            // The counter-scoped prompt wait is bound to this deploy command alone, carries the full
            // deploy budget, and already fails fast on a non-zero deploy via the `[n ERR:]`
            // prompt. (Unlike the sibling ACA/AKS tests, deploy here only runs `rad deploy` against
            // the already-provisioned AKS cluster, so the pipeline helper's transient-Azure-capacity
            // Assert.Skip -- which targets AKS *provisioning* -- does not apply.)
            // 17m preserves the previous effective ceiling (15m pipeline detect + 2m prompt) so the
            // single combined wait does not shrink the deploy's headroom.
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(17));

            // ===== PHASE 6: Verify the deployed application =====

            // The Radius application name is fixed to "app" by the publisher. Show the deployed
            // graph and the container resources; both must succeed.
            //
            // --preview forces the Radius.Core graph implementation. Without it, the pinned 0.59
            // `rad app graph` routes to the legacy Applications.Core graph API, which the legacy
            // `app` that Radius creates for Redis satisfies on its own -- so the command could
            // succeed without ever proving the Radius.Core UDT application that owns the project
            // containers actually deployed. See `rad app graph --help`:
            //   --preview   Use the Radius.Core preview implementation
            output.WriteLine("Step 23: Verifying Radius resources...");
            await auto.TypeAsync("rad app graph -a app --preview");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // An empty Radius.Core application still exits 0, and the harness only gates on the
            // shell exit code -- not on graph contents. Capture the preview graph once and assert
            // it names both project containers so a missing UDT container fails fast: grep exits
            // non-zero on a miss, and `G=$(...)` propagates a failed `rad app graph`, either of
            // which trips the `[n ERR:]` prompt that WaitForSuccessPromptAsync fails on.
            await auto.TypeAsync("G=$(rad app graph -a app --preview) && echo \"$G\" && echo \"$G\" | grep -q apiservice && echo \"$G\" | grep -q webfrontend");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            await auto.TypeAsync("rad resource list Radius.Compute/containers -a app");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(2));

            // Radius labels every workload with radapp.io/application; wait for all app pods ready.
            output.WriteLine("Step 24: Waiting for application pods to be ready...");
            await auto.TypeAsync($"kubectl wait --for=condition=ready pod -n {appNamespace} -l radapp.io/application=app --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Show labels too: the endpoint verification below resolves Services by the Radius
            // platform label radapp.io/resource=<name>. Printing --show-labels here makes a
            // label-schema mismatch (e.g. a different key/casing in a future control plane)
            // immediately visible in the recording instead of surfacing as an empty jsonpath.
            output.WriteLine("Step 25: Listing deployed pods and services...");
            await auto.TypeAsync($"kubectl get pods,svc -n {appNamespace} -l radapp.io/application=app --show-labels");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Resolve the apiservice Service by Radius resource label rather than a hardcoded
            // name (Radius names container services "<resource>-<container>").
            output.WriteLine("Step 26: Verifying apiservice endpoint via port-forward...");
            await auto.TypeAsync($"APISVC=$(kubectl get svc -n {appNamespace} -l radapp.io/resource=apiservice -o jsonpath='{{.items[0].metadata.name}}')");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));
            await auto.TypeAsync($"kubectl port-forward -n {appNamespace} svc/$APISVC 18080:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18080/weatherforecast -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Probe /weather (not /). The starter template wires Redis via AddRedisOutputCache("cache"),
            // and /weather is the only page that both calls the apiservice (@inject WeatherApiClient)
            // and is served through the Redis-backed output cache ([OutputCache(Duration = 5)]).
            // Requesting / would only render the static home page and could pass with a broken Redis
            // cache connection. The page uses [StreamRendering(true)], so a bare status check can
            // return 200 before the forecast loads; grep the streamed table (its "Temp." headers only
            // render in the forecasts != null branch, i.e. after WeatherApiClient returns) so the probe
            // proves the API + output-cache path end-to-end. curl reads the full streamed response.
            output.WriteLine("Step 27: Verifying webfrontend /weather endpoint (Redis output cache) via port-forward...");
            await auto.TypeAsync($"WEBSVC=$(kubectl get svc -n {appNamespace} -l radapp.io/resource=webfrontend -o jsonpath='{{.items[0].metadata.name}}')");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));
            await auto.TypeAsync($"kubectl port-forward -n {appNamespace} svc/$WEBSVC 18081:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18081/weather | grep -q Temp && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 28: Cleaning up port-forwards...");
            await auto.TypeAsync("kill %1 %2 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            output.WriteLine("Step 29: Exiting terminal...");
            await auto.TypeAsync("exit");
            await auto.EnterAsync();

            await pendingRun;

            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"Full Radius deployment completed in {duration}");

            DeploymentReporter.ReportDeploymentSuccess(
                nameof(DeployStarterTemplateToRadiusOnAks),
                resourceGroupName,
                new Dictionary<string, string>
                {
                    ["cluster"] = clusterName,
                    ["acr"] = acrName,
                    ["namespace"] = appNamespace,
                    ["project"] = projectName
                },
                duration);

            output.WriteLine("✅ Test passed - Aspire starter deployed to Radius on AKS!");
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            output.WriteLine($"❌ Test failed after {duration}: {ex.Message}");

            DeploymentReporter.ReportDeploymentFailure(
                nameof(DeployStarterTemplateToRadiusOnAks),
                resourceGroupName,
                ex.Message,
                ex.StackTrace);

            throw;
        }
        finally
        {
            // Deleting the resource group removes AKS, ACR, and all Radius control-plane state,
            // so no separate `rad` cleanup is required.
            output.WriteLine($"Cleaning up resource group: {resourceGroupName}");
            await CleanupResourceGroupAsync(resourceGroupName);
        }
    }

    private async Task CleanupResourceGroupAsync(string resourceGroupName)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = $"group delete --name {resourceGroupName} --yes --no-wait",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();
            using var waitCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(waitCts.Token);

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
