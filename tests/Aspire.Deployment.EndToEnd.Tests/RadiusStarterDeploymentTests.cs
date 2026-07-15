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

            var wsRoot = workspace.WorkspaceRoot.FullName;

            // Step 1a: Install the Radius (`rad`) CLI into the workspace. `aspire deploy` against a
            // Radius environment shells out to `rad deploy`, and this test drives `rad install
            // kubernetes` / `rad workspace create` / `rad app graph` directly, so the CLI must be on
            // PATH. Installing it here (rather than in the deployment-tests.yml workflow) keeps the
            // Radius prerequisite self-contained in the test that needs it: the ~dozens of other
            // deployment scenarios that share the workflow neither pay the download cost nor risk
            // failing on an install-endpoint outage, and the test can be run locally without any
            // workflow-specific setup.
            //
            // Install into workspace-local dirs so nothing leaks into the developer's real
            // ~/.rad, and prepend the CLI directory to PATH so the Step 1b `command -v rad` (and every
            // later `rad`) resolves to this binary. The Radius installer invokes `rad bicep download`,
            // and even `rad version --cli` initializes config, so run both with HOME scoped to the
            // installer subshell; Radius-managed tools and config then stay under the workspace home.
            // Export PATH outside the subshell because the CLI location must persist, while HOME must
            // not. Later az/kubectl/docker/aspire commands keep the real HOME and use their own
            // KUBECONFIG/DOCKER_CONFIG isolation where needed. radiusVersion pins the CLI version;
            // `rad install kubernetes` (Step 9) then installs the matching control plane, keeping the run
            // deterministic across Radius releases. Keep this aligned with
            // RadiusBicepExtension.Version (major.minor 0.59) so the installed control plane matches
            // the Bicep types the publisher emits. install.sh is fetched pinned to the immutable
            // commit SHA behind the v0.59.0 release tag (radiusInstallScriptSha) rather than a branch
            // or tag ref, either of which can be retargeted, so the executed installer content cannot
            // drift out from under this pin (supply-chain hardening). The download is retried a few
            // times to tolerate transient GitHub CDN failures on scheduled runs. install.sh's needsSudo
            // checks the install dir first and skips sudo when that directory already exists and is
            // writable; it only falls back to the parent dir when the install dir is absent. Pre-creating
            // the user-owned {wsRoot}/radbin makes it see a writable target and skip sudo entirely.
            const string radiusVersion = "0.59.0";

            // Immutable commit SHA that the v0.59.0 tag pointed to in radius-project/radius. Update this
            // together with radiusVersion (and RadiusBicepExtension.Version) when bumping the Radius
            // release, re-resolving the tag to its commit SHA.
            const string radiusInstallScriptSha = "2bf2c25fcdde20d4cba1371618829bbbe1f9a997";
            output.WriteLine("Step 1a: Installing the Radius (rad) CLI into the workspace...");
            await auto.TypeAsync(
                // `set -o pipefail` so a failed `curl` propagates through the pipe instead of being
                // masked by `install.sh`'s exit code; the interactive terminal shell does not enable
                // pipefail by default. Scoped to this compound command via a subshell so it never leaks
                // into later steps' commands. The install is retried up to three times (deleting any
                // partial binary between attempts) to ride out transient raw.githubusercontent.com or
                // release-asset download failures, mirroring the CLI E2E installer's retry loop.
                $"( set -o pipefail && " +
                $"mkdir -p \"{wsRoot}/radbin\" \"{wsRoot}/home\" && " +
                $"export HOME=\"{wsRoot}/home\" && " +
                $"{{ for i in 1 2 3; do " +
                $"curl -fsSL \"https://raw.githubusercontent.com/radius-project/radius/{radiusInstallScriptSha}/deploy/install.sh\" | " +
                $"/bin/bash -s -- --version \"{radiusVersion}\" --install-dir \"{wsRoot}/radbin\" && " +
                $"test -x \"{wsRoot}/radbin/rad\" && break; " +
                $"echo \"Retry $i: rad download failed, retrying in 5s...\"; " +
                $"rm -f \"{wsRoot}/radbin/rad\"; sleep 5; done; " +
                $"test -x \"{wsRoot}/radbin/rad\"; }} && " +
                $"\"{wsRoot}/radbin/rad\" version --cli ) && " +
                $"export PATH=\"{wsRoot}/radbin:$PATH\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            // Step 1b: Isolate kubeconfig, the Radius (`rad`) config, and Docker credentials to
            // throwaway files under the TemporaryWorkspace so this test never mutates the developer's
            // real ~/.kube/config, ~/.rad/config.yaml, or ~/.docker/config.json (test-isolation
            // requirement, .github/instructions/test-review-guidelines).
            //
            // - KUBECONFIG: az/kubectl honor it, so `az aks get-credentials` writes here and every
            //   kube-touching command uses this file. Removed with the workspace on dispose. Note
            //   several `rad` subcommands do NOT honor KUBECONFIG (they read the default
            //   ~/.kube/config), which is handled by the rad shim's workspace-local HOME below.
            // - rad config + HOME: `rad` only accepts a `--config` flag (no config env var), and
            //   `aspire deploy` spawns `rad deploy` internally (FileName="rad", UseShellExecute=false
            //   -> PATH lookup; see src/Aspire.Hosting.Radius/Publishing/RadiusDeploymentPipelineStep.cs).
            //   Several `rad` subcommands (e.g. `rad install kubernetes`'s Contour gateway config, and
            //   `rad workspace create kubernetes`) read the DEFAULT kubeconfig at $HOME/.kube/config
            //   directly and do NOT honor KUBECONFIG, unlike az/kubectl. So we shadow `rad` with a
            //   workspace-local shim earlier on PATH that (a) injects `--config <ws>/.rad/config.yaml`
            //   into every invocation (global flag, valid before any subcommand) and (b) exports
            //   HOME=<ws>/home, whose .kube/config is symlinked to our isolated $KUBECONFIG below.
            //   Setting HOME in the shim rather than on individual commands ensures EVERY `rad` -
            //   including the `rad deploy` that `aspire deploy` spawns, which we cannot env-prefix -
            //   reads the isolated kubeconfig, and it never touches the developer's real ~/.kube/config
            //   or ~/.rad. az/kubectl/docker/aspire keep the real HOME (they use KUBECONFIG/DOCKER_CONFIG
            //   env vars). REAL_RAD is resolved BEFORE prepending the shim dir, so the shim never
            //   recurses. The symlink target ($KUBECONFIG) is written by `az aks get-credentials`
            //   (Step 7) before the first `rad` runs (Step 9), so creating it here as a dangling link
            //   is fine. Everything lives under the TemporaryWorkspace and is removed on dispose, so
            //   there is nothing to back up or restore (avoiding a race with any concurrent `rad`).
            // - DOCKER_CONFIG: the documented override directory for Docker's config.json. `az acr login`
            //   shells out to `docker login`, and `dotnet publish /t:PublishContainer` reads the same
            //   config, so both authenticate against this throwaway dir instead of ~/.docker.
            output.WriteLine("Step 1b: Isolating kubeconfig, rad config, and docker credentials to the workspace...");
            await auto.TypeAsync(
                $"REAL_RAD=\"$(command -v rad)\" && " +
                $"mkdir -p \"{wsRoot}/bin\" \"{wsRoot}/.rad\" \"{wsRoot}/.kube\" \"{wsRoot}/.docker\" \"{wsRoot}/home/.kube\" && " +
                $"printf '#!/usr/bin/env bash\\nexport HOME=\"%s\"\\nexec \"%s\" --config \"%s\" \"$@\"\\n' \"{wsRoot}/home\" \"$REAL_RAD\" \"{wsRoot}/.rad/config.yaml\" > \"{wsRoot}/bin/rad\" && " +
                $"chmod +x \"{wsRoot}/bin/rad\" && " +
                $"ln -sf \"{wsRoot}/.kube/config\" \"{wsRoot}/home/.kube/config\" && " +
                $"export PATH=\"{wsRoot}/bin:$PATH\" && " +
                $"export KUBECONFIG=\"{wsRoot}/.kube/config\" && " +
                $"export DOCKER_CONFIG=\"{wsRoot}/.docker\"");
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
            // credentials are written to the isolated $DOCKER_CONFIG dir set up in Step 1b, not the
            // developer's ~/.docker/config.json.
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
                  // The test never SSHes into the nodes, so configure no SSH key at all. This avoids
                  // `--generate-ssh-keys`, which would write ~/.ssh/id_rsa[.pub] on a local run and
                  // mutate the developer's SSH state (the test isolates kube/rad/docker config in
                  // Step 1b; --no-ssh-key means there is simply nothing to isolate here).
                  $"--no-ssh-key " +
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

            // Install the Radius control plane. The `rad` CLI version is pinned by the Step 1a
            // install, and `rad install kubernetes` installs the matching control plane onto the
            // current kube context. This (and every other `rad`) runs through the Step 1b shim, so its
            // Contour gateway config - which reads the default-path kubeconfig ($HOME/.kube/config) and
            // ignores KUBECONFIG - resolves to our isolated config via the shim's workspace-local HOME,
            // and config writes land in the workspace-local <ws>/.rad/config.yaml.
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
            var webProgramFilePath = Path.Combine(projectDir, $"{projectName}.Web", "Program.cs");

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

            output.WriteLine($"Step 16b: Adding Radius diagnostics endpoints to Web Program.cs at: {webProgramFilePath}");
            var webProgram = File.ReadAllText(webProgramFilePath);
            if (!webProgram.Contains("using Microsoft.AspNetCore.OutputCaching;"))
            {
                webProgram = "using Microsoft.AspNetCore.OutputCaching;\n" + webProgram;
            }

            webProgram = webProgram.Replace(
                "app.MapRazorComponents<App>()",
                """
                app.MapGet("/radius-diagnostics/apiservice", async (WeatherApiClient weatherApi, CancellationToken cancellationToken) =>
                {
                    var forecasts = await weatherApi.GetWeatherAsync(cancellationToken: cancellationToken);
                    return Results.Ok(new { count = forecasts.Length });
                });

                app.MapGet("/radius-diagnostics/cache", async (IOutputCacheStore cacheStore, CancellationToken cancellationToken) =>
                {
                    var key = $"radius-diagnostics:{Guid.NewGuid():N}";
                    var payload = new byte[] { 1, 2, 3, 4 };

                    await cacheStore.SetAsync(key, payload, tags: null, validFor: TimeSpan.FromMinutes(1), cancellationToken);
                    var cached = await cacheStore.GetAsync(key, cancellationToken);

                    return cached is { Length: 4 } ? Results.Ok("OK") : Results.Problem("Redis output cache round-trip failed.");
                });

                app.MapRazorComponents<App>()
                """);

            File.WriteAllText(webProgramFilePath, webProgram);
            output.WriteLine("Modified Web Program.cs with direct apiservice and Redis output-cache diagnostics");

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
            // 17m is a generous upper bound for a single `rad deploy` of the starter app against the
            // already-provisioned AKS cluster -- Radius recipe execution, container image pulls, and
            // pod readiness -- plus margin for the terminal prompt to settle.
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

            // Print the deployed workloads (with labels) for diagnostics. Radius labels every
            // container Deployment/pod with radapp.io/resource=<name>, so this makes a label-schema
            // change in a future control plane immediately visible in the recording.
            output.WriteLine("Step 25: Listing deployed pods and services...");
            await auto.TypeAsync($"kubectl get pods,svc -n {appNamespace} -l radapp.io/application=app --show-labels");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            // Port-forward directly to the Deployment. Radius does not synthesize a Kubernetes
            // Service for Radius.Compute/containers workloads (only recipe-backed resources such as
            // the Redis cache get one), so there is no Service to target. kubectl port-forward
            // resolves a ready pod in the Deployment; 8080 is the container port Aspire assigns to
            // published containers (ASPNETCORE_HTTP_PORTS=8080).
            output.WriteLine("Step 26: Verifying apiservice endpoint via port-forward...");
            await auto.TypeAsync($"kubectl port-forward -n {appNamespace} deployment/apiservice 18080:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18080/weatherforecast -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Verify the webfrontend container serves HTTP by probing its home page, then verify
            // the Redis output-cache connection independently through a diagnostic endpoint added
            // above. We also add a direct uncached webfrontend -> apiservice diagnostic endpoint,
            // but deliberately do not assert it yet: the live Radius 0.59 behavior for Kubernetes
            // Service creation/naming from Radius.Compute/containers is not substantiated here.
            //
            // The current Radius resource-types-contrib Kubernetes container recipe does create a
            // ClusterIP Service for each container that declares ports. Its service config uses:
            //   name: '${normalizedName}-${svc.containerName}'
            //   port: port.value.containerPort
            //   targetPort: port.value.containerPort
            // where normalizedName is context.resource.name and svc.containerName is the key under
            // properties.containers. Aspire emits a single container entry keyed by the resource
            // name, so the candidate apiservice DNS name to try is:
            //   apiservice-apiservice.<namespace>.svc.cluster.local:8080
            // Source:
            // https://github.com/radius-project/resource-types-contrib/blob/main/Compute/containers/recipes/kubernetes/bicep/kubernetes-containers.bicep
            //
            // A live deploy-test/* run against the pinned rad 0.59 control plane must confirm the
            // exact Service name before asserting /radius-diagnostics/apiservice and /weather.
            output.WriteLine("Step 27: Verifying webfrontend home page via port-forward...");
            await auto.TypeAsync($"kubectl port-forward -n {appNamespace} deployment/webfrontend 18081:8080 &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18081/ -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 28: Verifying webfrontend -> Redis output-cache connectivity...");
            await auto.TypeAsync("for i in $(seq 1 10); do sleep 3 && curl -sf http://localhost:18081/radius-diagnostics/cache -o /dev/null -w '%{http_code}' && echo ' OK' && break; done");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            output.WriteLine("Step 29: Skipping direct apiservice and /weather assertions until Radius container Service DNS is live-validated.");

            output.WriteLine("Step 30: Cleaning up port-forwards...");
            await auto.TypeAsync("kill %1 %2 2>/dev/null; true");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(10));

            output.WriteLine("Step 31: Exiting terminal...");
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
            // so no separate `rad` cleanup is required. The isolated kubeconfig lived under the
            // TemporaryWorkspace (Step 1b/8b), so disposing the workspace is all the kube cleanup
            // needed — the developer's real ~/.kube/config was never touched.
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
            try
            {
                await process.WaitForExitAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (waitCts.IsCancellationRequested)
            {
                // WaitForExitAsync only stops awaiting on cancellation; it does NOT terminate the
                // spawned `az` process. Disposing the Process object wouldn't either. Kill the whole
                // tree -- `az` is a Python wrapper that forks child processes -- so a hung cleanup
                // (e.g. an interactive auth-refresh prompt) can't outlive the test run. Then wait
                // briefly for the OS to reap the tree so we don't report before it has actually exited.
                var terminated = false;
                try
                {
                    process.Kill(entireProcessTree: true);
                    using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await process.WaitForExitAsync(killCts.Token);
                    terminated = true;
                }
                catch (Exception killEx)
                {
                    // The process may have exited between the timeout and the kill (race), or the
                    // kill/second wait may itself fail; report based on whether the wait completed.
                    output.WriteLine($"Failed to terminate timed-out cleanup process: {killEx.Message}");
                }

                var detail = terminated
                    ? "Timed out after 2 minutes; process tree terminated"
                    : "Timed out after 2 minutes; process tree may not be fully terminated";
                output.WriteLine($"Resource group deletion timed out ({resourceGroupName}): {detail}");
                DeploymentReporter.ReportCleanupStatus(resourceGroupName, success: false, detail);
                return;
            }

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
