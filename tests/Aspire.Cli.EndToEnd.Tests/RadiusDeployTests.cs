// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for deploying an AppHost that targets a Radius compute
/// environment (see <c>Aspire.Hosting.Radius</c>) all the way to running
/// workloads — <b>without any Azure</b>. Where <see cref="RadiusPublishTests"/>
/// stops at generating <c>app.bicep</c>, this test drives the full CLI path
/// (<c>aspire publish</c> → <c>aspire deploy</c> → <c>rad deploy app.bicep</c>)
/// against a local KinD cluster with the Radius control plane installed, then
/// asserts the container is actually scheduled and serving HTTP.
///
/// This gives per-PR, local coverage of the Radius deploy flow alongside the
/// live Azure/AKS test (<c>Aspire.Deployment.EndToEnd.Tests</c>), which runs on
/// demand (<c>workflow_dispatch</c>) and nightly (the <c>deployment-tests.yml</c>
/// schedule), not on every PR.
///
/// A public image (<c>mcr.microsoft.com/dotnet/samples:aspnetapp</c>) is used
/// so the KinD node pulls it directly from MCR. That intentionally avoids the
/// build-and-push-to-localhost:5001 machinery the Kubernetes deploy tests need:
/// no image build, no registry round-trip, and no reliance on the mounted host
/// Docker daemon for image movement — the single biggest reliability win for a
/// per-PR test. The KinD cluster is still created via
/// <see cref="KubernetesDeployTestHelpers.CreateKindClusterWithRegistryAsync"/>
/// (the registry sits idle) because that helper also performs the critical
/// internal-kubeconfig networking fix that lets the helper container reach the
/// cluster's API server.
/// </summary>
public sealed class RadiusDeployTests(ITestOutputHelper output)
{
    private const string ProjectName = "AspireRadiusDeployTest";

    // A stable, digest-pinned public image. The `dotnet/samples` images are explicitly documented
    // as unstable and can break at any time (dotnet/dotnet-docker#7191), so this test uses the same
    // image + digest the deployment E2E suite standardized on (see
    // tests/Aspire.Deployment.EndToEnd.Tests/AcaCompactNamingDeploymentTests.cs). Pinning by SHA256
    // makes the pulled content immutable, so the KinD node pulls the exact bytes once from MCR.
    private const string ContainerImage = "mcr.microsoft.com/azuredocs/aci-helloworld";
    private const string ContainerImageTag = "latest";
    private const string ContainerImageDigest = "456a1150aa41340a14c7be1342deda2cde9e6e7df9fde6b8a69de0ae04f92fad";
    private const int ContainerPort = 80;

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployRadiusContainerToKind()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();

        // The Radius app namespace must be a valid RFC 1123 label (WithNamespace
        // enforces this) and must pre-exist before deploy: the Radius.Core
        // environment controller hard-fails if the target namespace is missing
        // (the UDT environment model, unlike the legacy Applications.Core model,
        // deliberately does not auto-create it).
        var radiusNamespace = $"radius-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Radius namespace: {radiusNamespace}");

        // mountDockerSocket: true is required so KinD (and the Radius control-plane
        // images it pulls) run against the host Docker daemon from inside the
        // helper container.
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.VerifyPullRequestCliVersionAsync(counter);

        try
        {
            // =================================================================
            // Phase 1: Cluster + Radius control plane
            // =================================================================
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);
            await auto.InstallRadCliAsync(counter);
            await auto.InstallRadiusControlPlaneAsync(counter, clusterName);

            // =================================================================
            // Phase 2: Scaffold the AppHost
            // =================================================================

            // Empty AppHost template (not Starter): the Radius publisher fails on
            // ProjectResources with no attached image, so we add exactly one
            // container. This mirrors RadiusPublishTests.
            await auto.AspireNewCSharpEmptyAppHostAsync(ProjectName, counter);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire add Aspire.Hosting.Radius");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            // Insert the Radius wiring before `builder.Build().Run();`. AddRadiusEnvironment,
            // WithNamespace, AddContainer, and WithHttpEndpoint are all non-[Experimental],
            // so no ASPIRERADIUS*/ASPIREPIPELINES* suppression is needed. WithHttpEndpoint's
            // targetPort drives the container port the Radius publisher emits on the native
            // Radius.Compute/containers workload. Radius does not synthesize a Kubernetes Service
            // for that workload, so Phase 5 reaches it by port-forwarding straight to the
            // Deployment rather than through a Service.
            var appHostFilePath = Path.Combine(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);
            const string buildRunPattern = "builder.Build().Run();";
            Assert.Contains(buildRunPattern, content);
            var radiusWiring = $$"""
                builder.AddRadiusEnvironment("radius").WithNamespace("{{radiusNamespace}}");
                builder.AddContainer("web", "{{ContainerImage}}", "{{ContainerImageTag}}")
                    .WithImageSHA256("{{ContainerImageDigest}}")
                    .WithHttpEndpoint(targetPort: {{ContainerPort}});
                """;
            content = content.Replace(buildRunPattern, radiusWiring + Environment.NewLine + Environment.NewLine + buildRunPattern);
            File.WriteAllText(appHostFilePath, content);

            // ASPIRE_PLAYGROUND=true takes precedence over --non-interactive and makes
            // Spectre.Console attempt concurrent dynamic displays (see KubernetesPublishTests).
            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // =================================================================
            // Phase 3: Publish and assert the generated Bicep shape
            // =================================================================
            await auto.TypeAsync("aspire publish -o radius-output --non-interactive");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            var appBicepPath = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName, "radius-output", "app.bicep");
            Assert.True(File.Exists(appBicepPath), $"Expected generated Bicep at '{appBicepPath}'.");
            var appBicep = File.ReadAllText(appBicepPath);
            Assert.Contains("Radius.Core/environments", appBicep);
            Assert.Contains("Radius.Compute/containers", appBicep);
            Assert.Contains(ContainerImage, appBicep);

            // =================================================================
            // Phase 4: Create the app namespace, then deploy
            // =================================================================
            await auto.TypeAsync($"kubectl create namespace {radiusNamespace} --context kind-{clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // aspire deploy regenerates the artifacts and runs `rad deploy app.bicep`
            // against the radius-e2e workspace (pinned to this KinD cluster). A
            // container-only Radius app has no parameters to prompt for.
            //
            // Wait on this command's own sequence-numbered prompt with the full deploy
            // budget rather than WaitForPipelineSuccessAsync: the latter scans the whole
            // viewport and would match the stale "Pipeline succeeded" left by the earlier
            // `aspire publish`, returning before this deploy finishes. The prompt wait is
            // scoped to this command and still fails fast on a non-zero deploy via the ERR
            // prompt.
            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(15));

            // =================================================================
            // Phase 5: Verify the workload is scheduled and serving HTTP
            // =================================================================

            // Radius labels every workload it creates with radapp.io/application and
            // radapp.io/resource; wait on the app label so we don't depend on the
            // generated Deployment/pod name.
            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod -n {radiusNamespace} -l radapp.io/application=app --timeout=180s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(4));

            await auto.TypeAsync($"kubectl get pods,svc -n {radiusNamespace} -l radapp.io/application=app");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // Resolve the Deployment by the radapp.io/resource label and port-forward to it
            // directly. Radius does not synthesize a Kubernetes Service for a container workload
            // (the HTTP endpoint is modeled at the Radius layer, not as a k8s Service), so there
            // is no Service to target; only the Deployment/pods exist. Resolving by label avoids
            // depending on the generated Deployment name.
            await auto.TypeAsync($"RADIUS_DEPLOY=$(kubectl get deployment -n {radiusNamespace} -l radapp.io/resource=web -o jsonpath='{{.items[0].metadata.name}}') && echo \"Resolved deployment: $RADIUS_DEPLOY\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync($"kubectl port-forward -n {radiusNamespace} deployment/$RADIUS_DEPLOY 18080:{ContainerPort} &");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("sleep 3");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The aspnetapp sample serves HTTP 200 on `/`. Retry to absorb the brief
            // window while the port-forward and container finish coming up. The success
            // marker is split in the shell source (VERIFY''_OK evaluates to VERIFY_OK) so
            // the contiguous token appears only in curl's output on a 200, never in the
            // echoed command line — otherwise WaitUntilTextAsync would match the command
            // itself and return before curl succeeds. Mirrors BICEP_IMAGES''_OK in the
            // AKS deployment test.
            await auto.TypeAsync("for i in $(seq 1 20); do " +
                "code=$(curl -s -o /dev/null -w '%{http_code}' http://localhost:18080/ 2>/dev/null); " +
                "if [ \"$code\" = \"200\" ]; then echo VERIFY''_OK; break; fi; " +
                "echo \"Attempt $i: got http=$code, retrying...\"; sleep 5; done");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("VERIFY_OK", timeout: TimeSpan.FromMinutes(3));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            await auto.TypeAsync("kill %1 2>/dev/null || true");
            await auto.EnterAsync();
            await auto.WaitForAnyPromptAsync(counter);

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }

    /// <summary>
    /// Deploys a container that reaches a PostgreSQL database provisioned by a Radius recipe, and
    /// proves the projected connection values actually authenticate against the deployed database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DeployRadiusContainerToKind"/> covers only a container workload, and the AKS
    /// deployment test covers Redis, which is emitted as a legacy <c>Applications.*</c> type and
    /// therefore exercises the <c>listSecrets()</c> branch. Neither proves anything about the
    /// <c>Radius.*</c> UDT branch, where Aspire has to write <c>username</c>/<c>password</c>/
    /// <c>database</c> onto the resource for the recipe to consume. Only a real deployment can show
    /// that the targeted Radius version accepts those properties and that the credential handed to
    /// the consumer is the one the recipe provisioned.
    /// </para>
    /// <para>
    /// The check runs <c>psql</c> from a throwaway pod using the values read back out of the
    /// <em>deployed</em> consumer's env, rather than from the generated Bicep: that is the only way
    /// to observe what the deploy actually resolved. The pod reuses the same
    /// <c>postgres:16-alpine</c> image the recipe already pulled onto the node
    /// (<c>--image-pull-policy=IfNotPresent</c>), so the test adds no new registry round-trip.
    /// </para>
    /// </remarks>
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployRadiusPostgresBackingResourceToKind()
    {
        const string PostgresProjectName = "AspireRadiusPostgresDeployTest";

        // Matches the image tag the Radius Kubernetes PostgreSQL recipe deploys, so the verification
        // pod runs from an image already present on the node.
        // https://github.com/radius-project/resource-types-contrib/blob/main/Data/postgreSqlDatabases/recipes/kubernetes/bicep/kubernetes-postgresql.bicep
        const string PostgresImage = "postgres:16-alpine";

        // `rad install kubernetes` registers the built-in Radius.Compute/*, Radius.Core/* and
        // Applications.* types, but the Radius.Data/* types Aspire emits for PostgreSQL live in
        // resource-types-contrib and are installed per cluster. Pinned to a commit
        // rather than main so an upstream schema change cannot silently alter what this asserts.
        const string PostgresTypeManifestUrl =
            "https://raw.githubusercontent.com/radius-project/resource-types-contrib/39f65be914c931acce42bc730ebdedff6fcc7af7/Data/postgreSqlDatabases/postgreSqlDatabases.yaml";

        // The value bound to the @secure() `pg_password` parameter Aspire emits. Passing it
        // explicitly is what makes the assertion meaningful: the same literal has to come back out
        // of the deployed container's environment *and* authenticate against the database the
        // recipe provisioned, which is only true if it reached the resource's `password` property.
        const string PostgresPassword = "AspireE2E-pg-1";

        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);

        var clusterName = KubernetesDeployTestHelpers.GenerateUniqueClusterName();
        var radiusNamespace = $"radius-{clusterName[..16]}";

        output.WriteLine($"Cluster name: {clusterName}");
        output.WriteLine($"Radius namespace: {radiusNamespace}");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.VerifyPullRequestCliVersionAsync(counter);

        try
        {
            // =================================================================
            // Phase 1: Cluster + Radius control plane
            // =================================================================
            await auto.InstallKindAndHelmAsync(counter);
            await auto.CreateKindClusterWithRegistryAsync(counter, clusterName);
            await auto.InstallRadCliAsync(counter);
            await auto.InstallRadiusControlPlaneAsync(counter, clusterName);

            await auto.TypeAsync($"curl -fsSL -o /tmp/postgresqldatabases.yaml {PostgresTypeManifestUrl} && rad resource-type create postgreSqlDatabases --from-file /tmp/postgresqldatabases.yaml");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            // =================================================================
            // Phase 2: Scaffold the AppHost
            // =================================================================
            await auto.AspireNewCSharpEmptyAppHostAsync(PostgresProjectName, counter);

            await auto.TypeAsync($"cd {PostgresProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire add Aspire.Hosting.Radius");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            await auto.TypeAsync("aspire add Aspire.Hosting.PostgreSQL");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            // The database password is the parameter Aspire generates for run mode. The publisher
            // emits it as a @secure() Bicep parameter, writes it onto the Radius resource's
            // `password` property for the recipe, and composes the same value into the consumer's
            // connection values — the agreement this test verifies end to end.
            var appHostFilePath = Path.Combine(
                workspace.WorkspaceRoot.FullName,
                PostgresProjectName,
                "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);
            const string buildRunPattern = "builder.Build().Run();";
            Assert.Contains(buildRunPattern, content);
            var radiusWiring = $$"""
                builder.AddRadiusEnvironment("radius").WithNamespace("{{radiusNamespace}}");
                var appdb = builder.AddPostgres("pg").AddDatabase("appdb");
                builder.AddContainer("web", "{{ContainerImage}}", "{{ContainerImageTag}}")
                    .WithImageSHA256("{{ContainerImageDigest}}")
                    .WithHttpEndpoint(targetPort: {{ContainerPort}})
                    .WithReference(appdb);
                """;
            content = content.Replace(buildRunPattern, radiusWiring + Environment.NewLine + Environment.NewLine + buildRunPattern);
            File.WriteAllText(appHostFilePath, content);

            await auto.TypeAsync("unset ASPIRE_PLAYGROUND");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // =================================================================
            // Phase 3: Publish and assert the emitted resource shape
            // =================================================================
            await auto.TypeAsync("aspire publish -o radius-output --non-interactive");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

            var appBicepPath = Path.Combine(workspace.WorkspaceRoot.FullName, PostgresProjectName, "radius-output", "app.bicep");
            Assert.True(File.Exists(appBicepPath), $"Expected generated Bicep at '{appBicepPath}'.");
            var appBicep = File.ReadAllText(appBicepPath);
            Assert.Contains("Radius.Data/postgreSqlDatabases", appBicep);

            // username/password/database are `required` schema properties on the resource, read by
            // the recipe as context.resource.properties.<name>. Emitting them anywhere else (for
            // example under properties.recipe.parameters) fails schema validation before the recipe
            // runs, which is precisely what the deploy below would catch.
            Assert.Contains("username: 'postgres'", appBicep);
            Assert.Contains("database: 'appdb'", appBicep);

            // =================================================================
            // Phase 4: Create the app namespace, then deploy
            // =================================================================
            await auto.TypeAsync($"kubectl create namespace {radiusNamespace} --context kind-{clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // The Bicep extension `rad` resolves (br:biceptypes.azurecr.io/radius:<version>) carries
            // types only for the built-in namespaces, so a contrib type such as
            // Radius.Data/postgreSqlDatabases compiles to a *classic* ARM resource (BCP081, "does
            // not have types available") and the deployment engine routes it to the Azure provider:
            //   "Azure deployment failed, please ensure you have configured an Azure provider..."
            // Registering the type with the control plane is not enough; the compiler needs it too.
            // Generating a local extension from the same manifest and importing it is the supported
            // workaround, and it is what a user deploying an Aspire-generated Radius app with a
            // PostgreSQL or SQL Server resource has to do today.
            await auto.TypeAsync("rad bicep publish-extension --from-file /tmp/postgresqldatabases.yaml --target radius-output/aspire-udt.tgz --force");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(3));

            await auto.TypeAsync("sed -i 's#\"extensions\": {#\"extensions\": {\\n        \"aspireudt\": \"./aspire-udt.tgz\",#' radius-output/bicepconfig.json && " +
                "sed -i '1a extension aspireudt' radius-output/app.bicep && head -3 radius-output/app.bicep");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // `rad deploy` on the published output rather than `aspire deploy`, because the latter
            // republishes and would discard the extension import added above. The artifacts under
            // test are still exactly the ones Aspire generated.
            await auto.TypeAsync($"rad deploy radius-output/app.bicep --group default -p pg_password={PostgresPassword}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(20));

            // =================================================================
            // Phase 5: Verify the projected credentials reach the database
            // =================================================================
            await auto.TypeAsync($"kubectl wait --for=condition=Available deployment -n {radiusNamespace} -l radapp.io/resource=pg --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod -n {radiusNamespace} -l radapp.io/resource=web --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            // Read the values out of the *deployed* consumer rather than the generated Bicep: only
            // the deployed spec shows what Radius actually resolved the recipe outputs and the
            // @secure() parameter to. `WithReference(appdb)` splats the connection properties as
            // APPDB_* alongside ConnectionStrings__appdb.
            var webDeployment = $"kubectl get deployment -n {radiusNamespace} -l radapp.io/resource=web -o jsonpath='{{.items[0].metadata.name}}'";
            await auto.TypeAsync($"WEB_DEPLOY=$({webDeployment}) && echo \"Resolved deployment: $WEB_DEPLOY\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            var envValue = $"kubectl get deployment -n {radiusNamespace} $WEB_DEPLOY -o jsonpath=";
            await auto.TypeAsync(
                $"PGHOST=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"APPDB_HOST\")].value}}') && " +
                $"PGPORT=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"APPDB_PORT\")].value}}') && " +
                $"PGUSER=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"APPDB_USERNAME\")].value}}') && " +
                $"PGDATABASE=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"APPDB_DATABASENAME\")].value}}') && " +
                $"PGPASSWORD=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"APPDB_PASSWORD\")].value}}') && " +
                "echo \"projected host=$PGHOST port=$PGPORT user=$PGUSER database=$PGDATABASE password_length=${#PGPASSWORD}\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The consumer must receive the very parameter that was written onto the resource's
            // `password` property, not a recipe-generated one — that agreement is what makes the
            // connection string usable.
            await auto.TypeAsync($"test \"$PGPASSWORD\" = '{PostgresPassword}' && echo PWMATCH''_OK");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("PWMATCH_OK", timeout: TimeSpan.FromSeconds(60));
            await auto.WaitForSuccessPromptAsync(counter);

            // A wrong password, user, or database name fails here rather than producing a silently
            // misconfigured app — the failure mode https://github.com/microsoft/aspire/issues/18935
            // describes. The success marker is split in the shell source (PGVERIFY''_OK evaluates to
            // PGVERIFY_OK) so the contiguous token appears only in the loop's output, never in the
            // echoed command line.
            await auto.TypeAsync("for i in $(seq 1 20); do " +
                $"if kubectl run pgcheck$i -n {radiusNamespace} --rm -i --restart=Never --image={PostgresImage} " +
                "--image-pull-policy=IfNotPresent --env=PGPASSWORD=\"$PGPASSWORD\" --command -- " +
                "psql -h \"$PGHOST\" -p \"$PGPORT\" -U \"$PGUSER\" -d \"$PGDATABASE\" -tAc 'select 1' | grep -q '^1$'; " +
                "then echo PGVERIFY''_OK; break; fi; " +
                "echo \"Attempt $i: psql could not connect, retrying...\"; sleep 10; done");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("PGVERIFY_OK", timeout: TimeSpan.FromMinutes(5));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(1));

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }
}
