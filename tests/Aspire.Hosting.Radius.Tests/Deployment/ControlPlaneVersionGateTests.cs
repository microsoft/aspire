// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Experimental: the pipeline step graph is under test.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace Aspire.Hosting.Radius.Tests.Deployment;

/// <summary>
/// The generated Bicep targets the Radius v0.60 schemas, and an older control plane drops the
/// fields it does not recognize instead of rejecting them — so a v0.59 cluster reports a successful
/// deploy and produces an application whose backing resources have no recipe. The deploy step turns
/// that into a loud failure, which only works if the control plane version is read correctly.
/// </summary>
public class ControlPlaneVersionGateTests
{
    /// <summary>
    /// The shape `rad version -o json` emits, from the CLI's <c>CombinedVersionInfo</c>:
    /// https://github.com/radius-project/radius/blob/main/pkg/cli/cmd/version/version.go.
    /// </summary>
    [Theory]
    [InlineData("""{"cli":{"release":"0.60.0","version":"v0.60.0","bicep":"0.35.1","commit":"abc"},"controlPlane":{"version":"0.60.0","status":"Installed"}}""", "0.60.0")]
    [InlineData("""{"controlPlane":{"version":"0.59.0","status":"Installed"}}""", "0.59.0")]
    [InlineData("""{"controlPlane":{"version":"v0.61.2","status":"Installed"}}""", "0.61.2")]
    public void ControlPlaneVersion_IsReadFromRadVersionJson(string json, string expected)
    {
        Assert.True(RadiusDeploymentPipelineStep.TryParseControlPlaneVersion(json, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    /// <summary>
    /// Every payload the CLI emits when it cannot report a version has to read as *unknown*, never
    /// as an old version: the gate exists to convert one silent failure into a loud one, and must
    /// not become a new way for an otherwise valid deploy to fail.
    /// </summary>
    [Theory]
    // Cluster unreachable, or Radius not installed on it — the CLI still exits 0.
    [InlineData("""{"controlPlane":{"version":"Not installed","status":"Not connected"}}""")]
    // An edge/dev build of the control plane.
    [InlineData("""{"controlPlane":{"version":"edge","status":"Installed"}}""")]
    // `rad version --cli`, or a CLI predating the combined payload.
    [InlineData("""{"cli":{"release":"0.60.0","version":"v0.60.0"}}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    // `version` present but not a string — must read as unknown rather than throwing.
    [InlineData("""{"controlPlane":{"version":59,"status":"Installed"}}""")]
    [InlineData("""{"controlPlane":{"version":{"major":0},"status":"Installed"}}""")]
    [InlineData("""{"controlPlane":"Installed"}""")]
    public void UnreadableControlPlaneVersion_IsTreatedAsUnknown(string json)
    {
        Assert.False(RadiusDeploymentPipelineStep.TryParseControlPlaneVersion(json, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void UnsupportedControlPlaneException_NamesTheVersionsAndTheRemediation()
    {
        var ex = RadiusDeploymentPipelineStep.CreateUnsupportedControlPlaneException(new Version(0, 59), "kind-radius");

        Assert.Contains("0.59", ex.Message, StringComparison.Ordinal);
        Assert.Contains(RadiusDeploymentPipelineStep.MinimumControlPlaneVersion.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("rad upgrade kubernetes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ASPIRERADIUS091", ex.Message, StringComparison.Ordinal);
        // The context is in the message because the gate deliberately inspects the workspace's
        // cluster rather than the ambient one; naming it makes a surprising verdict diagnosable.
        Assert.Contains("kind-radius", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Setting <c>KUBECONFIG</c> is not enough to aim <c>rad version</c> at the workspace's cluster:
    /// parts of the Radius CLI load <c>clientcmd.RecommendedHomeFile</c> (<c>~/.kube/config</c>)
    /// directly and ignore <c>KUBECONFIG</c>, so a supported ambient cluster could let an
    /// unsupported workspace target pass the gate — exactly the silent broken deployment the gate
    /// exists to prevent. The isolation therefore has to redirect the home directory as well, on
    /// every variable client-go's <c>homedir.HomeDir()</c> consults.
    /// </summary>
    [Fact]
    public void ControlPlaneGate_IsolatesTheHomeKubeconfigRadActuallyReads()
    {
        var home = Path.Combine(Path.GetTempPath(), "aspire-radius-kubeconfig-test");
        var expectedKubeConfig = Path.Combine(home, ".kube", "config");

        var environment = RadiusDeploymentPipelineStep.BuildIsolatedKubeConfigEnvironment(
            home,
            realHome: null,
            getEnvironmentVariable: static _ => null);

        // KUBECONFIG must name the file *inside* the redirected home, so the loaders that honor it
        // and the loaders that hardcode ~/.kube/config resolve to the same minified kubeconfig.
        Assert.Equal(expectedKubeConfig, environment["KUBECONFIG"]);
        Assert.Equal(home, environment["HOME"]);
        Assert.Equal(home, environment["USERPROFILE"]);
        // Removed, not merely blanked: homedir.HomeDir() only skips the HOMEDRIVE+HOMEPATH pair when
        // either is empty, and a surviving pair would point Windows back at the real profile.
        Assert.Null(environment["HOMEDRIVE"]);
        Assert.Null(environment["HOMEPATH"]);
    }

    /// <summary>
    /// <c>--flatten</c> inlines file-backed credentials but carries kubeconfig <c>exec</c> entries
    /// through untouched, and every managed cluster authenticates that way (<c>kubelogin</c> for
    /// AKS, <c>aws eks get-token</c> for EKS, <c>gke-gcloud-auth-plugin</c> for GKE). Those helpers
    /// resolve their own credentials under the home directory, so a bare redirect would leave them
    /// with an empty home, fail the authentication, and fail <c>rad version</c> — and because the
    /// gate fails open on a version it cannot read, it would silently skip for exactly the users it
    /// is most needed for. Each helper's state is pinned back to the real home instead.
    /// </summary>
    [Fact]
    public void ControlPlaneGate_KeepsExecCredentialHelpersPointedAtTheRealHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "aspire-radius-kubeconfig-test");
        var realHome = Path.Combine(Path.GetTempPath(), "aspire-radius-real-home");

        var environment = RadiusDeploymentPipelineStep.BuildIsolatedKubeConfigEnvironment(
            home,
            realHome,
            getEnvironmentVariable: static _ => null);

        // The kubeconfig stays isolated; only the credential state follows the real home.
        Assert.Equal(Path.Combine(home, ".kube", "config"), environment["KUBECONFIG"]);
        Assert.Equal(home, environment["HOME"]);

        Assert.Equal(Path.Combine(realHome, ".azure"), environment["AZURE_CONFIG_DIR"]);
        Assert.Equal(Path.Combine(realHome, ".aws", "config"), environment["AWS_CONFIG_FILE"]);
        Assert.Equal(Path.Combine(realHome, ".aws", "credentials"), environment["AWS_SHARED_CREDENTIALS_FILE"]);
        Assert.Equal(Path.Combine(realHome, ".config", "gcloud"), environment["CLOUDSDK_CONFIG"]);
    }

    /// <summary>
    /// A credential-helper location the user has already relocated must win: the child inherits the
    /// ambient environment, and an explicit value may deliberately point outside the home. Only the
    /// unset variables are pinned.
    /// </summary>
    [Fact]
    public void ControlPlaneGate_DoesNotOverrideAnExplicitCredentialHelperLocation()
    {
        var home = Path.Combine(Path.GetTempPath(), "aspire-radius-kubeconfig-test");
        var realHome = Path.Combine(Path.GetTempPath(), "aspire-radius-real-home");

        var environment = RadiusDeploymentPipelineStep.BuildIsolatedKubeConfigEnvironment(
            home,
            realHome,
            getEnvironmentVariable: static name => name is "AZURE_CONFIG_DIR" ? "/custom/azure" : null);

        Assert.False(environment.ContainsKey("AZURE_CONFIG_DIR"));
        Assert.Equal(Path.Combine(realHome, ".config", "gcloud"), environment["CLOUDSDK_CONFIG"]);
    }

    /// <summary>
    /// With no resolvable home there is nothing to pin the helpers to, so the isolation is applied
    /// on its own rather than pointing them at paths built from an empty string.
    /// </summary>
    [Fact]
    public void ControlPlaneGate_WithNoResolvableRealHome_PinsNothing()
    {
        var home = Path.Combine(Path.GetTempPath(), "aspire-radius-kubeconfig-test");

        var environment = RadiusDeploymentPipelineStep.BuildIsolatedKubeConfigEnvironment(
            home,
            realHome: string.Empty,
            getEnvironmentVariable: static _ => null);

        Assert.Equal(
            new[] { "HOME", "HOMEDRIVE", "HOMEPATH", "KUBECONFIG", "USERPROFILE" },
            environment.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The gate is a separate step precisely so it runs before anything mutates the cluster or the
    /// machine: registering cloud credentials rewrites installation-global <c>rad</c> state and
    /// applying sealed secrets writes to the cluster. Folding it back into the deploy step, or
    /// dropping one of these edges, would silently restore the ordering bug — which no
    /// parsing-level test can observe.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlPlaneGate_RunsBeforeEveryStepThatMutatesTheClusterOrTheMachine(bool withCloudProvider)
    {
        var steps = await CreateEnvironmentStepsAsync("myenv", withCloudProvider);

        var gate = Assert.Single(steps, step => step.Name == "verify-radius-control-plane-myenv");

        Assert.Contains("deploy-radius-myenv", gate.RequiredBySteps);
        Assert.Contains("apply-sealed-secrets-myenv", gate.RequiredBySteps);
        Assert.Equal(withCloudProvider, gate.RequiredBySteps.Contains("register-radius-credentials-myenv"));
    }

    /// <summary>
    /// The gate contacts the cluster, which is deploy-only work: <c>aspire publish</c> must keep
    /// emitting artifacts on a machine with no cluster (or no <c>rad</c>) at all. Depending on
    /// <c>DeployPrereq</c> and being required only by deploy-side steps is what keeps it out of the
    /// publish graph.
    /// </summary>
    [Fact]
    public async Task ControlPlaneGate_IsNotPartOfThePublishGraph()
    {
        var steps = await CreateEnvironmentStepsAsync("myenv", withCloudProvider: false);

        var gate = Assert.Single(steps, step => step.Name == "verify-radius-control-plane-myenv");

        Assert.Equal([WellKnownPipelineSteps.DeployPrereq], gate.DependsOnSteps);
        Assert.DoesNotContain("publish-radius-myenv", gate.RequiredBySteps);
    }

    /// <summary>
    /// The end-to-end gate: a control plane below the minimum has to stop the deploy. The parsing
    /// and message tests above pin the pieces, but only this exercises the path that decides —
    /// issuing the commands, reading the result and throwing — so a regression that mishandles a
    /// successful response cannot pass unnoticed while the deployment tests keep installing a
    /// supported v0.60 control plane.
    /// </summary>
    [Fact]
    public async Task ControlPlaneGate_WithAnUnsupportedControlPlane_FailsTheDeploy()
    {
        var runner = new RecordingCommandRunner(controlPlaneVersion: "0.59.0");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RadiusDeploymentPipelineStep.VerifyControlPlaneVersionAsync(
                NullLogger.Instance,
                "kind-radius",
                runner.RunAsync,
                CancellationToken.None));

        Assert.Contains("ASPIRERADIUS091", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0.59.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kind-radius", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart: the minimum supported version must proceed. Pinned alongside the failure
    /// case so a change that makes the comparison reject everything — turning the gate into a
    /// blanket deploy failure — is caught here rather than by users on a supported cluster.
    /// </summary>
    [Fact]
    public async Task ControlPlaneGate_WithASupportedControlPlane_AllowsTheDeploy()
    {
        var runner = new RecordingCommandRunner(controlPlaneVersion: "0.60.0");

        await RadiusDeploymentPipelineStep.VerifyControlPlaneVersionAsync(
            NullLogger.Instance,
            "kind-radius",
            runner.RunAsync,
            CancellationToken.None);

        Assert.Equal(["kubectl", "rad"], runner.Invocations.Select(invocation => invocation.FileName));
    }

    /// <summary>
    /// The gate has to inspect the workspace's cluster, not whichever one is ambient. That means
    /// exporting the workspace context and running <c>rad</c> against an isolated home containing
    /// the exported kubeconfig — asserted here on the real invocations rather than on the
    /// environment builder alone, so the wiring between the two cannot silently come apart.
    /// </summary>
    [Fact]
    public async Task ControlPlaneGate_RunsRadAgainstTheExportedWorkspaceKubeconfig()
    {
        var runner = new RecordingCommandRunner(controlPlaneVersion: "0.60.0");

        await RadiusDeploymentPipelineStep.VerifyControlPlaneVersionAsync(
            NullLogger.Instance,
            "kind-radius",
            runner.RunAsync,
            CancellationToken.None);

        var export = runner.Invocations[0];
        Assert.Equal(["config", "view", "--raw", "--minify", "--flatten", "--context", "kind-radius", "--output", "yaml"], export.Arguments);
        Assert.Null(export.Environment);

        var version = runner.Invocations[1];
        Assert.Equal(["version", "--output", "json"], version.Arguments);
        var kubeConfig = Assert.Contains("KUBECONFIG", version.Environment!);

        // The export has to be the file `rad` reads, and it has to still be on disk while `rad`
        // runs: the gate deletes the temporary home afterwards, so capturing the contents here is
        // the only point at which that can be verified.
        Assert.Equal(Path.Combine(version.Environment!["HOME"]!, ".kube", "config"), kubeConfig);
        Assert.Equal(RecordingCommandRunner.ExportedKubeConfig, version.KubeConfigContentsAtInvocation);
    }

    /// <summary>
    /// The gate fails open on anything it cannot read, because it exists to convert one silent
    /// failure into a loud one and must never become a new way for a valid deploy to fail. Each
    /// case here is a way the environment can be uncooperative rather than unsupported: no
    /// resolvable workspace context, no <c>kubectl</c> on PATH, an export that fails, no <c>rad</c>
    /// on PATH, and a <c>rad</c> that fails.
    /// </summary>
    [Fact]
    public async Task ControlPlaneGate_WithAnUnreadableEnvironment_SkipsInsteadOfFailing()
    {
        await RadiusDeploymentPipelineStep.VerifyControlPlaneVersionAsync(
            NullLogger.Instance, kubeContext: null, ThrowingRunner, CancellationToken.None);

        await RunWithAsync((fileName, _) => fileName == "kubectl" ? null : new ProcessRunResult(0, ""));
        await RunWithAsync((fileName, _) => fileName == "kubectl" ? new ProcessRunResult(1, "") : new ProcessRunResult(0, ""));
        await RunWithAsync((fileName, _) => fileName == "kubectl" ? new ProcessRunResult(0, "apiVersion: v1") : null);
        await RunWithAsync((fileName, _) => fileName == "kubectl" ? new ProcessRunResult(0, "apiVersion: v1") : new ProcessRunResult(1, ""));

        // A successful `rad` whose payload carries no readable control plane version. Guarded here
        // as well as in the parsing tests because reaching the comparison with an unknown version
        // would fail every deploy on a cluster the gate simply cannot describe.
        await RunWithAsync((fileName, _) => fileName == "kubectl"
            ? new ProcessRunResult(0, "apiVersion: v1")
            : new ProcessRunResult(0, """{"controlPlane":{"version":"edge","status":"Installed"}}"""));

        static Task<ProcessRunResult?> ThrowingRunner(
            string fileName, string[] arguments, IReadOnlyDictionary<string, string?>? environment, CancellationToken cancellationToken)
            => throw new XunitException($"The gate ran '{fileName}' with no resolvable workspace context.");

        static Task RunWithAsync(Func<string, string[], ProcessRunResult?> run)
            => RadiusDeploymentPipelineStep.VerifyControlPlaneVersionAsync(
                NullLogger.Instance,
                "kind-radius",
                (fileName, arguments, environment, cancellationToken) => Task.FromResult(run(fileName, arguments)),
                CancellationToken.None);
    }

    private sealed class RecordingCommandRunner(string controlPlaneVersion)
    {
        internal const string ExportedKubeConfig = "apiVersion: v1\nkind: Config\nclusters: []\n";

        public List<Invocation> Invocations { get; } = [];

        public Task<ProcessRunResult?> RunAsync(
            string fileName,
            string[] arguments,
            IReadOnlyDictionary<string, string?>? environment,
            CancellationToken _)
        {
            // Read the exported kubeconfig now rather than after the gate returns: the gate deletes
            // the temporary home in its finally block, so this is the only moment it exists.
            var kubeConfigPath = environment?.GetValueOrDefault("KUBECONFIG");
            var kubeConfigContents = kubeConfigPath is not null && File.Exists(kubeConfigPath)
                ? File.ReadAllText(kubeConfigPath)
                : null;

            Invocations.Add(new Invocation(fileName, arguments, environment, kubeConfigContents));

            return Task.FromResult<ProcessRunResult?>(fileName switch
            {
                "kubectl" => new ProcessRunResult(0, ExportedKubeConfig),
                "rad" => new ProcessRunResult(
                    0,
                    $$$"""{"controlPlane":{"version":"{{{controlPlaneVersion}}}","status":"Installed"}}"""),
                _ => throw new XunitException($"The gate ran an unexpected command: '{fileName}'."),
            });
        }

        internal sealed record Invocation(
            string FileName,
            string[] Arguments,
            IReadOnlyDictionary<string, string?>? Environment,
            string? KubeConfigContentsAtInvocation);
    }

    private static async Task<List<PipelineStep>> CreateEnvironmentStepsAsync(string environmentName, bool withCloudProvider)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddRadiusEnvironment(environmentName);
        if (withCloudProvider)
        {
            environment.WithAzureProvider(
                "00000000-0000-0000-0000-000000000000",
                "rg",
                azure => azure.WithServicePrincipal(
                    "00000000-0000-0000-0000-000000000001",
                    "00000000-0000-0000-0000-000000000002",
                    builder.AddParameter("clientsecret", "secret", secret: true)));
        }

        var annotation = Assert.Single(environment.Resource.Annotations.OfType<PipelineStepAnnotation>());
        var steps = await annotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = environment.Resource,
        });

        return steps.ToList();
    }
}
