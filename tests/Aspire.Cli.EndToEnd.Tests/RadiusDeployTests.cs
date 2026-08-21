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
    /// deployment test covers Redis. Neither proves anything about the <c>Radius.*</c> UDT branch,
    /// where Aspire has to write <c>username</c>/<c>password</c>/<c>database</c> onto the resource
    /// for the recipe to consume. Only a real deployment can show that the targeted Radius version
    /// accepts those properties and that the credential handed to the consumer is the one the
    /// recipe provisioned.
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

        // `rad install kubernetes` on Radius 0.60 registers Radius.Data/postgreSqlDatabases, and the
        // pinned Bicep extension carries its types, so no per-cluster type registration is needed.
        // The type is still absent from the default Kubernetes recipe pack
        // (https://github.com/radius-project/resource-types-contrib/issues/276), so Aspire pins
        // `kube-recipes/postgresqldatabases` in the recipe pack it emits — which is exactly what
        // `aspire deploy` below exercises.

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

            // username/password are `required` schema properties on the resource, read by the
            // recipe as context.resource.properties.<name>. `database` is optional and defaults to
            // `postgres_db` when omitted, but this AppHost references a specific database via
            // AddDatabase(...), so it is still emitted here. Emitting the required properties
            // anywhere else (for example under properties.recipe.parameters) fails schema
            // validation before the recipe runs, which is precisely what the deploy below would
            // catch.
            Assert.Contains("username: 'postgres'", appBicep);
            Assert.Contains("database: 'appdb'", appBicep);

            // =================================================================
            // Phase 4: Create the app namespace, then deploy
            // =================================================================
            await auto.TypeAsync($"kubectl create namespace {radiusNamespace} --context kind-{clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            // Radius 0.60 ships Radius.Data/postgreSqlDatabases in the pinned `radius` Bicep
            // extension and registers it at `rad install kubernetes`, so the artifacts Aspire
            // generated deploy as-is: no local extension to publish, no import to splice in, and
            // therefore no reason to bypass `aspire deploy`.
            //
            // `aspire deploy` regenerates the artifacts and runs `rad deploy app.bicep`. It
            // generates its own owner-only parameters file for the @secure() `pg_password`
            // parameter and cannot consume an externally supplied one, so the password is never
            // known to this test — see the verification below for how the agreement is proven
            // without it.
            //
            // Wait on this command's own sequence-numbered prompt with the full deploy budget
            // rather than WaitForPipelineSuccessAsync, which would match the stale "Pipeline
            // succeeded" left by the earlier `aspire publish`.
            await auto.TypeAsync("aspire deploy");
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
                "echo \"projected host=$PGHOST port=$PGPORT user=$PGUSER database=$PGDATABASE\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The password is *not* among them: a credential-bearing variable is published as
            // `valueFrom.secretKeyRef`, so `.value` is empty for it and the password has to be read
            // out of the referenced Secret. Reading `.value` here would silently hand psql an empty
            // password and fail below as a confusing authentication error.
            await auto.TypeAsync(
                $"PG_SECRET=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"APPDB_PASSWORD\")].valueFrom.secretKeyRef.name}}') && " +
                $"PG_SECRET_KEY=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"APPDB_PASSWORD\")].valueFrom.secretKeyRef.key}}') && " +
                "test -n \"$PG_SECRET\" && test \"$PG_SECRET_KEY\" = APPDB_PASSWORD && " +
                "echo \"secret ref: $PG_SECRET/$PG_SECRET_KEY\" && echo SECRETREF''_OK");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("SECRETREF_OK", timeout: TimeSpan.FromSeconds(60));
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync(
                $"PGPASSWORD=$(kubectl get secret -n {radiusNamespace} \"$PG_SECRET\" -o jsonpath=\"{{.data.$PG_SECRET_KEY}}\" | base64 -d) && " +
                "echo \"password_length=${#PGPASSWORD}\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // The password is generated by `aspire deploy` into its own owner-only parameters file,
            // so this test cannot compare it against a known literal. The agreement under test is
            // preserved by proving it *authenticates*: the value projected to the consumer must be
            // the same one written onto the resource's `password` property that the recipe consumed,
            // or the psql login below fails. Assert it is non-empty first so an empty projection
            // fails here with a clear message rather than as a confusing psql auth error.
            await auto.TypeAsync("test -n \"$PGPASSWORD\" && echo PWPRESENT''_OK");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("PWPRESENT_OK", timeout: TimeSpan.FromSeconds(60));
            await auto.WaitForSuccessPromptAsync(counter);

            // A wrong password, user, or database name fails here rather than producing a silently
            // misconfigured app — the failure mode https://github.com/microsoft/aspire/issues/18935
            // describes. The success marker is split in the shell source (PGVERIFY''_OK evaluates to
            // PGVERIFY_OK) so the contiguous token appears only in the loop's output, never in the
            // echoed command line.
            //
            // The attempt count and the wait below are a pair: 12 attempts sleep 120s in total, and
            // each attempt also schedules a pod and may pull the image, so the loop's worst case has
            // to stay comfortably inside the wait or the test fails with a timeout while it is still
            // legitimately retrying.
            await auto.TypeAsync("for i in $(seq 1 12); do " +
                $"if kubectl run pgcheck$i -n {radiusNamespace} --rm -i --restart=Never --image={PostgresImage} " +
                "--image-pull-policy=IfNotPresent --env=PGPASSWORD=\"$PGPASSWORD\" --command -- " +
                "psql -h \"$PGHOST\" -p \"$PGPORT\" -U \"$PGUSER\" -d \"$PGDATABASE\" -tAc 'select 1' | grep -q '^1$'; " +
                "then echo PGVERIFY''_OK; break; fi; " +
                "echo \"Attempt $i: psql could not connect, retrying...\"; sleep 10; done");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("PGVERIFY_OK", timeout: TimeSpan.FromMinutes(8));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(1));

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }

    /// <summary>
    /// Deploys Redis and RabbitMQ, which moved from the legacy <c>Applications.*</c> portable types
    /// onto their <c>Radius.*</c> UDTs in Radius 0.60, and proves the credentials Aspire projects
    /// are the ones the deployed servers actually enforce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These two types are the only ones whose migration cannot be validated from a Bicep snapshot.
    /// RabbitMQ's <c>password</c> property takes the <em>resource ID of a
    /// <c>Radius.Security/secrets</c> resource</em>, not a password string, so the snapshot can only
    /// show that a reference was emitted — not that Radius dereferenced it, materialized the secret,
    /// and handed the value to the recipe. A wrong secret shape fails at deploy time or, worse,
    /// provisions a broker with a credential the consumer was never told about. Only a real deploy
    /// distinguishes those.
    /// </para>
    /// <para>
    /// Redis is the mirror case: its recipe deploys an <em>unauthenticated</em> server, so the
    /// correct behaviour is that no credential is projected at all (ASPIRERADIUS075). That is a
    /// claim about the deployed server's configuration, which the generated Bicep cannot make.
    /// </para>
    /// <para>
    /// Both checks read from the <em>deployed</em> consumer's env rather than the generated Bicep,
    /// because only the deployed spec shows what Radius resolved the recipe outputs and the
    /// <c>@secure()</c> parameter to.
    /// </para>
    /// </remarks>
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DeployRadiusRedisAndRabbitMqBackingResourcesToKind()
    {
        const string ProjectName = "AspireRadiusCacheQueueDeployTest";

        // Matches the image tag the Radius Kubernetes Redis recipe deploys, so the verification pod
        // runs from an image already present on the node.
        // https://github.com/radius-project/resource-types-contrib/blob/main/Data/redisCaches/recipes/kubernetes/bicep/kubernetes-redis.bicep
        const string RedisImage = "redis:7-alpine";

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

            // =================================================================
            // Phase 2: Scaffold the AppHost
            // =================================================================
            await auto.AspireNewCSharpEmptyAppHostAsync(ProjectName, counter);

            await auto.TypeAsync($"cd {ProjectName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("aspire add Aspire.Hosting.Radius");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            await auto.TypeAsync("aspire add Aspire.Hosting.RabbitMQ");
            await auto.EnterAsync();
            await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromSeconds(180));

            var appHostFilePath = Path.Combine(
                workspace.WorkspaceRoot.FullName,
                ProjectName,
                "apphost.cs");
            var content = File.ReadAllText(appHostFilePath);
            const string buildRunPattern = "builder.Build().Run();";
            Assert.Contains(buildRunPattern, content);
            var radiusWiring = $$"""
                builder.AddRadiusEnvironment("radius").WithNamespace("{{radiusNamespace}}");
                var cache = builder.AddRedis("cache");
                // An explicit non-`guest` user name is required: RabbitMQ restricts `guest` to
                // loopback connections, so a broker provisioned with it would reject the `web` pod.
                // Publishing a bare AddRabbitMQ fails with ASPIRERADIUS082 for that reason.
                var queue = builder.AddRabbitMQ("queue", userName: builder.AddParameter("queueuser", "appuser"));
                builder.AddContainer("web", "{{ContainerImage}}", "{{ContainerImageTag}}")
                    .WithImageSHA256("{{ContainerImageDigest}}")
                    .WithHttpEndpoint(targetPort: {{ContainerPort}})
                    .WithReference(cache)
                    .WithReference(queue);
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

            var appBicepPath = Path.Combine(workspace.WorkspaceRoot.FullName, ProjectName, "radius-output", "app.bicep");
            Assert.True(File.Exists(appBicepPath), $"Expected generated Bicep at '{appBicepPath}'.");
            var appBicep = File.ReadAllText(appBicepPath);

            // The 0.60 UDTs, not the legacy portable types they replaced.
            Assert.Contains("Radius.Data/redisCaches", appBicep);
            Assert.Contains("Radius.Messaging/rabbitMQ@", appBicep);
            Assert.DoesNotContain("Applications.Datastores/redisCaches", appBicep);
            Assert.DoesNotContain("Applications.Messaging/rabbitMQQueues", appBicep);

            // RabbitMQ's password is a reference to a secret resource, never a literal on the
            // broker. If this ever regresses to an inline password the deploy below still succeeds
            // — Radius would store the string as the "secret ID" — so pin the shape here and let
            // the deploy prove it resolves.
            Assert.Contains("Radius.Security/secrets", appBicep);
            Assert.Contains("password: queue_password_secret.id", appBicep);

            // Radius.Security/secrets is recipe-backed, so emitting one obliges the pack to carry
            // its recipe. Without this entry the deploy below fails resolving a recipe for the
            // secret rather than for the broker that pulled it in.
            Assert.Contains("ghcr.io/radius-project/kube-recipes/secrets:latest", appBicep);

            // The consumer's credentials are secret references too, not clear-text container env.
            // `QUEUE_USERNAME` stays a plain value: it is not a credential, and routing it through a
            // secret would make the deployed spec needlessly unreadable.
            Assert.Contains("resource web_env_secret 'Radius.Security/secrets@", appBicep);
            Assert.Contains("secretName: 'web-env-secret'", appBicep);

            // =================================================================
            // Phase 4: Create the app namespace, then deploy
            // =================================================================
            await auto.TypeAsync($"kubectl create namespace {radiusNamespace} --context kind-{clusterName}");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(60));

            await auto.TypeAsync("aspire deploy");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(20));

            // =================================================================
            // Phase 5: Verify against the deployed servers
            // =================================================================
            await auto.TypeAsync($"kubectl wait --for=condition=Available deployment -n {radiusNamespace} -l radapp.io/resource=cache --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            await auto.TypeAsync($"kubectl wait --for=condition=Available deployment -n {radiusNamespace} -l radapp.io/resource=queue --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            await auto.TypeAsync($"kubectl wait --for=condition=Ready pod -n {radiusNamespace} -l radapp.io/resource=web --timeout=300s");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(6));

            var webDeployment = $"kubectl get deployment -n {radiusNamespace} -l radapp.io/resource=web -o jsonpath='{{.items[0].metadata.name}}'";
            await auto.TypeAsync($"WEB_DEPLOY=$({webDeployment}) && echo \"Resolved deployment: $WEB_DEPLOY\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // `WithReference(...)` splats each resource's connection properties as CACHE_*/QUEUE_*
            // alongside ConnectionStrings__cache / ConnectionStrings__queue. Only the non-credential
            // ones carry a `.value`; the password is a secret reference and is read further down.
            var envValue = $"kubectl get deployment -n {radiusNamespace} $WEB_DEPLOY -o jsonpath=";
            await auto.TypeAsync(
                $"REDIS_HOST=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"CACHE_HOST\")].value}}') && " +
                $"REDIS_PORT=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"CACHE_PORT\")].value}}') && " +
                $"MQ_USER=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"QUEUE_USERNAME\")].value}}') && " +
                "echo \"projected redis=$REDIS_HOST:$REDIS_PORT mq_user=$MQ_USER\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            // ---------------------------------------------------------------
            // Credentials reach the pod as a secret reference, not as clear text
            // ---------------------------------------------------------------
            // This is the claim that can only be proven against a real cluster: the publisher emits
            // `valueFrom.secretKeyRef`, but whether Radius's container recipe carries that through
            // to the Deployment, and whether the secrets recipe created a Kubernetes Secret under
            // the name the reference uses, is decided by the recipes rather than by Aspire.
            await auto.TypeAsync(
                $"MQ_SECRET=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"QUEUE_PASSWORD\")].valueFrom.secretKeyRef.name}}') && " +
                $"MQ_SECRET_KEY=$({envValue}'{{.spec.template.spec.containers[0].env[?(@.name==\"QUEUE_PASSWORD\")].valueFrom.secretKeyRef.key}}') && " +
                "test -n \"$MQ_SECRET\" && test \"$MQ_SECRET_KEY\" = QUEUE_PASSWORD && " +
                "echo \"secret ref: $MQ_SECRET/$MQ_SECRET_KEY\" && echo SECRETREF''_OK");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("SECRETREF_OK", timeout: TimeSpan.FromSeconds(60));
            await auto.WaitForSuccessPromptAsync(counter);

            // The Deployment spec must not carry the credential in any form. This is the whole point
            // of the secret reference: the spec and its rollout history are readable by anyone with
            // `get deployment` in the namespace.
            //
            // Setup and assertion are deliberately separate commands. Bash binds `||` to the entire
            // preceding `&&` chain, so folding the fetches into the grep would let any setup failure
            // — a missing deployment, an empty secret, a failed base64 decode — short-circuit
            // straight to the NOPLAINTEXT_OK marker and report success without ever running the
            // check. Each setup step is verified by its own success prompt first.
            await auto.TypeAsync(
                $"kubectl get deployment -n {radiusNamespace} $WEB_DEPLOY -o json > /tmp/webdeploy.json && " +
                "kubectl get secret -n " + radiusNamespace + " \"$MQ_SECRET\" -o jsonpath='{.data.QUEUE_PASSWORD}' | base64 -d > /tmp/mqpw.txt && " +
                "MQ_PASSWORD=$(cat /tmp/mqpw.txt) && test -n \"$MQ_PASSWORD\" && echo \"fetched deployment spec and a ${#MQ_PASSWORD}-char password\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(120));

            // Only the grep's own result can select a marker here.
            await auto.TypeAsync(
                "grep -q -- \"$MQ_PASSWORD\" /tmp/webdeploy.json && echo LEAKED || echo NOPLAINTEXT''_OK");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("NOPLAINTEXT_OK", timeout: TimeSpan.FromSeconds(120));
            await auto.WaitForSuccessPromptAsync(counter);

            // Finally, the value the *process* sees. A reference that resolves to nothing would
            // still satisfy every assertion above, and the broker check below would then fail as a
            // confusing authentication error rather than as a missing value.
            await auto.TypeAsync(
                $"WEB_POD=$(kubectl get pod -n {radiusNamespace} -l radapp.io/resource=web -o jsonpath='{{.items[0].metadata.name}}') && " +
                $"MQ_PASSWORD=$(kubectl exec -n {radiusNamespace} \"$WEB_POD\" -- printenv QUEUE_PASSWORD) && " +
                "test \"$MQ_PASSWORD\" = \"$(cat /tmp/mqpw.txt)\" && echo \"mq_password_length=${#MQ_PASSWORD}\" && echo INJECTED''_OK");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("INJECTED_OK", timeout: TimeSpan.FromMinutes(2));
            await auto.WaitForSuccessPromptAsync(counter);

            // ---------------------------------------------------------------
            // Redis: reachable, and intentionally unauthenticated
            // ---------------------------------------------------------------
            // A bare PING succeeding is the whole claim: it proves the projected host/port address
            // the deployed server *and* that the server accepts commands without AUTH. If a future
            // recipe starts Redis with --requirepass, PING returns NOAUTH and this fails — which is
            // the signal that ASPIRERADIUS075 and the NoCredential mapping have to be revisited,
            // not a flake.
            await auto.TypeAsync("for i in $(seq 1 12); do " +
                $"if kubectl run rediscacheck$i -n {radiusNamespace} --rm -i --restart=Never --image={RedisImage} " +
                "--image-pull-policy=IfNotPresent --command -- " +
                "redis-cli -h \"$REDIS_HOST\" -p \"$REDIS_PORT\" ping | grep -q '^PONG$'; " +
                "then echo REDISVERIFY''_OK; break; fi; " +
                "echo \"Attempt $i: redis-cli could not connect, retrying...\"; sleep 10; done");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("REDISVERIFY_OK", timeout: TimeSpan.FromMinutes(8));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(1));

            // ---------------------------------------------------------------
            // RabbitMQ: the projected credentials authenticate
            // ---------------------------------------------------------------
            // The password is generated by `aspire deploy` into its own owner-only parameters file,
            // so it cannot be compared against a known literal — proving it *authenticates* is the
            // equivalent guarantee. Non-emptiness was already established above.

            // The user name the AppHost supplied must be the one both provisioned on the broker
            // and projected to the consumer. If the emission regresses to the UDT default the
            // broker is provisioned as `radius` and this mismatch surfaces here.
            await auto.TypeAsync("test \"$MQ_USER\" = appuser && echo MQUSER''_OK");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("MQUSER_OK", timeout: TimeSpan.FromSeconds(60));
            await auto.WaitForSuccessPromptAsync(counter);

            // `rabbitmqctl authenticate_user` runs inside the broker pod and checks the credential
            // against the broker's own user database, which is exactly the question being asked:
            // did the value Aspire put in the Radius.Security/secrets resource reach the recipe and
            // get provisioned as this user's password? Running it in-pod also avoids depending on
            // the management plugin or on an AMQP client image.
            //
            // Being in-pod means this does not exercise RabbitMQ's loopback restriction, which
            // applies to the `guest` account and would reject a client in another pod. That is
            // covered structurally instead: publishing refuses to emit `guest` at all
            // (ASPIRERADIUS082), so no deployment can reach that state.
            await auto.TypeAsync($"MQ_POD=$(kubectl get pod -n {radiusNamespace} -l radapp.io/resource=queue -o jsonpath='{{.items[0].metadata.name}}') && echo \"Resolved broker pod: $MQ_POD\"");
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter);

            await auto.TypeAsync("for i in $(seq 1 12); do " +
                $"if kubectl exec -n {radiusNamespace} \"$MQ_POD\" -- " +
                "rabbitmqctl authenticate_user \"$MQ_USER\" \"$MQ_PASSWORD\"; " +
                "then echo MQVERIFY''_OK; break; fi; " +
                "echo \"Attempt $i: broker not ready or credentials rejected, retrying...\"; sleep 10; done");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("MQVERIFY_OK", timeout: TimeSpan.FromMinutes(8));
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(1));

            await auto.CleanupKubernetesDeploymentAsync(counter, clusterName);
        }
        finally
        {
            await KubernetesDeployTestHelpers.CleanupKindClusterOutOfBandAsync(clusterName, output);
        }
    }
}
