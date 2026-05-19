// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for installing cert-manager into a Kubernetes environment
/// and declaring <c>ClusterIssuer</c> resources against it.
/// </summary>
public static class CertManagerExtensions
{
    // The pinned default cert-manager chart version. Bump deliberately when validating against
    // a newer release; the Helm chart's API and CRDs evolve across minor versions.
    private const string DefaultChartReference = "oci://quay.io/jetstack/charts/cert-manager";
    private const string DefaultChartVersion = "v1.20.2";

    // Well-known ACME directory endpoints. See https://letsencrypt.org/docs/acme-protocol-updates/
    // for the current canonical URLs.
    private const string LetsEncryptProductionUrl = "https://acme-v02.api.letsencrypt.org/directory";
    private const string LetsEncryptStagingUrl = "https://acme-staging-v02.api.letsencrypt.org/directory";

    // The annotation cert-manager watches on Gateway / Ingress resources to auto-provision
    // a Certificate from the named ClusterIssuer.
    // See https://cert-manager.io/docs/usage/gateway/ and
    // https://cert-manager.io/docs/usage/ingress/.
    internal const string ClusterIssuerAnnotationKey = "cert-manager.io/cluster-issuer";

    // Aspire resource names cap at 64 chars. The helm chart we register beside the wrapper
    // is "{name}-chart" (6 extra chars), so the user-facing name has to stay under this
    // bound or AddHelmChart will reject it with a confusing downstream error.
    private const int MaxResourceNameLength = 64;
    private const string ChartNameSuffix = "-chart";

    /// <summary>
    /// Installs cert-manager into the Kubernetes environment and returns a typed
    /// <see cref="CertManagerResource"/> that can host issuer resources.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The Aspire resource name for the cert-manager installation. Each call
    /// adds a uniquely-named resource to the application model, so multiple Kubernetes environments
    /// must each pass distinct names.</param>
    /// <param name="chartVersion">The cert-manager Helm chart version to install.
    /// Defaults to a pinned version validated against this Aspire build.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerResource}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Internally creates a <see cref="KubernetesHelmChartResource"/> via
    /// <see cref="KubernetesHelmChartExtensions.AddHelmChart(IResourceBuilder{KubernetesEnvironmentResource}, string, string, string)"/>
    /// pointed at <c>oci://quay.io/jetstack/charts/cert-manager</c>. The chart is configured with:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>crds.enabled = true</c> — installs the cert-manager CRDs (<c>ClusterIssuer</c>, <c>Certificate</c>, ...) so issuer manifests can be applied immediately afterwards.</item>
    ///   <item><c>config.enableGatewayAPI = true</c> — lets cert-manager watch Gateway API <c>Gateway</c>/<c>HTTPRoute</c> resources for the cluster-issuer annotation.</item>
    ///   <item><c>WithForceConflicts()</c> — works around the AKS Azure Policy add-on mutating cert-manager's <c>ValidatingWebhookConfiguration</c> after install.</item>
    ///   <item><c>WithDestroy()</c> — uninstalls the Helm release on <c>aspire destroy</c>.</item>
    /// </list>
    /// <para>
    /// Issuer manifests are applied directly via <c>kubectl apply</c> at deploy time (not as
    /// part of the Helm release), and are deleted via <c>kubectl delete</c> on
    /// <c>aspire destroy</c> before the cert-manager Helm release itself is uninstalled.
    /// </para>
    /// <para>
    /// To customise additional Helm values, access the underlying chart via
    /// <see cref="CertManagerResource.HelmChart"/>.
    /// </para>
    /// </remarks>
    [AspireExport(Description = "Installs cert-manager into a Kubernetes environment")]
    public static IResourceBuilder<CertManagerResource> AddCertManager(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name,
        string? chartVersion = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Keep room for the "-chart" suffix we append for the underlying helm chart resource.
        // Without this guard, an otherwise-valid 64-char name would build successfully here
        // and then explode inside AddHelmChart with a less-clear "name too long" message.
        if (name.Length > MaxResourceNameLength - ChartNameSuffix.Length)
        {
            throw new ArgumentException(
                $"cert-manager resource name '{name}' is too long. The maximum length is {MaxResourceNameLength - ChartNameSuffix.Length} characters because " +
                $"a companion Helm chart resource is registered as '{{name}}{ChartNameSuffix}' which must itself fit within the {MaxResourceNameLength}-character Aspire resource name limit.",
                nameof(name));
        }

        var version = chartVersion ?? DefaultChartVersion;

        // The helm chart is exposed in the model under "{name}-chart" so the user-facing
        // CertManagerResource can keep the natural "{name}" identifier without colliding.
        // Both show up in the dashboard / generated artifacts: the chart is what actually
        // installs cert-manager, and the wrapper is what hosts the typed issuer children.
        var chartName = $"{name}{ChartNameSuffix}";

        var chartBuilder = builder
            .AddHelmChart(chartName, DefaultChartReference, version)
            .WithHelmValue("crds.enabled", "true")
            // Gateway API support is opt-in in the cert-manager chart. Without these values
            // cert-manager will not provision Certificates for Gateway listeners.
            // See https://cert-manager.io/docs/usage/gateway/.
            .WithHelmValue("config.apiVersion", "controller.config.cert-manager.io/v1alpha1")
            .WithHelmValue("config.kind", "ControllerConfiguration")
            .WithHelmValue("config.enableGatewayAPI", "true")
            .WithForceConflicts()
            .WithDestroy();

        var resource = new CertManagerResource(name, builder.Resource, chartBuilder.Resource);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(resource);
        }

        var resourceBuilder = builder.ApplicationBuilder.AddResource(resource).ExcludeFromManifest();

        // Emit one kubectl-apply step per ClusterIssuer at deploy time, plus one kubectl-delete
        // step per ClusterIssuer at destroy time. The annotation factory closures capture the
        // CertManagerResource and read .Issuers when the pipeline is assembled, so issuers added
        // via AddIssuer(...) after this call are still picked up.
        // Apply steps depend on helm-install-{chartName} so cert-manager (and its admission
        // webhook) is up before we apply CRD instances. Delete steps run before
        // helm-uninstall-{chartName} so we delete the ClusterIssuers (and let cert-manager
        // clean up its account secrets) while the controller is still alive.
        resourceBuilder.WithAnnotation(new PipelineStepAnnotation(_ =>
            BuildIssuerApplySteps(resource, chartName)));
        resourceBuilder.WithAnnotation(new PipelineStepAnnotation(_ =>
            BuildIssuerDeleteSteps(resource, chartName)));

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a cert-manager <c>ClusterIssuer</c> to this cert-manager installation.
    /// </summary>
    /// <param name="builder">The cert-manager resource builder.</param>
    /// <param name="name">The Aspire resource name. Also used as the <c>metadata.name</c>
    /// of the generated <c>ClusterIssuer</c>, so it must be a valid DNS-1123 label.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport(Description = "Adds a cert-manager ClusterIssuer")]
    public static IResourceBuilder<CertManagerIssuerResource> AddIssuer(
        this IResourceBuilder<CertManagerResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var issuer = new CertManagerIssuerResource(name, builder.Resource);
        builder.Resource.Issuers.Add(issuer);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(issuer);
        }

        return builder.ApplicationBuilder.AddResource(issuer).ExcludeFromManifest();
    }

    /// <summary>
    /// Configures the issuer to use the Let's Encrypt production ACME endpoint.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="email">The contact email registered with the ACME account. Let's Encrypt
    /// uses this address for expiry notifications and rate-limit appeals.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    /// <remarks>
    /// Production certificates are subject to strict per-domain rate limits
    /// (<see href="https://letsencrypt.org/docs/rate-limits/"/>). For development workflows,
    /// prefer <see cref="WithLetsEncryptStaging(IResourceBuilder{CertManagerIssuerResource}, string)"/>
    /// which uses untrusted staging certificates with much higher rate limits.
    /// </remarks>
    [AspireExport(Description = "Configures the issuer for Let's Encrypt production")]
    public static IResourceBuilder<CertManagerIssuerResource> WithLetsEncryptProduction(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        string email)
        => WithAcmeServer(builder, LetsEncryptProductionUrl, email);

    /// <summary>
    /// Configures the issuer to use the Let's Encrypt production ACME endpoint, with the
    /// contact email supplied via a parameter resolved at deploy time.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="email">A parameter resource builder whose value is the contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport("withLetsEncryptProductionParam", Description = "Configures the issuer for Let's Encrypt production with a parameterized email")]
    public static IResourceBuilder<CertManagerIssuerResource> WithLetsEncryptProduction(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        IResourceBuilder<ParameterResource> email)
        => WithAcmeServer(builder, LetsEncryptProductionUrl, email);

    /// <summary>
    /// Configures the issuer to use the Let's Encrypt staging ACME endpoint. Certificates issued
    /// from staging are not trusted by browsers, but the endpoint has much higher rate limits,
    /// making it the right choice for development and CI workflows.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="email">The contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport(Description = "Configures the issuer for Let's Encrypt staging")]
    public static IResourceBuilder<CertManagerIssuerResource> WithLetsEncryptStaging(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        string email)
        => WithAcmeServer(builder, LetsEncryptStagingUrl, email);

    /// <summary>
    /// Configures the issuer to use the Let's Encrypt staging ACME endpoint, with the
    /// contact email supplied via a parameter resolved at deploy time.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="email">A parameter resource builder whose value is the contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport("withLetsEncryptStagingParam", Description = "Configures the issuer for Let's Encrypt staging with a parameterized email")]
    public static IResourceBuilder<CertManagerIssuerResource> WithLetsEncryptStaging(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        IResourceBuilder<ParameterResource> email)
        => WithAcmeServer(builder, LetsEncryptStagingUrl, email);

    /// <summary>
    /// Configures the issuer to use a custom ACME directory endpoint (e.g., a private ACME
    /// server such as ZeroSSL or step-ca).
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="serverUrl">The ACME directory URL (e.g., <c>https://acme.example.com/directory</c>).</param>
    /// <param name="email">The contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport(Description = "Configures the issuer to use a custom ACME directory")]
    public static IResourceBuilder<CertManagerIssuerResource> WithAcmeServer(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        string serverUrl,
        string email)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);
        ArgumentException.ThrowIfNullOrEmpty(email);

        builder.Resource.Spec = new CertManagerAcmeIssuerSpec(
            ReferenceExpression.Create($"{serverUrl}"),
            ReferenceExpression.Create($"{email}"));

        return builder;
    }

    /// <summary>
    /// Configures the issuer to use a custom ACME directory endpoint with a parameterized email.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="serverUrl">The ACME directory URL (e.g., <c>https://acme.example.com/directory</c>).</param>
    /// <param name="email">A parameter resource builder whose value is the contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    [AspireExport("withAcmeServerParam", Description = "Configures the issuer to use a custom ACME directory with a parameterized email")]
    public static IResourceBuilder<CertManagerIssuerResource> WithAcmeServer(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        string serverUrl,
        IResourceBuilder<ParameterResource> email)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(serverUrl);
        ArgumentNullException.ThrowIfNull(email);

        builder.Resource.Spec = new CertManagerAcmeIssuerSpec(
            ReferenceExpression.Create($"{serverUrl}"),
            ReferenceExpression.Create($"{email.Resource}"));

        return builder;
    }

    /// <summary>
    /// Adds an HTTP-01 ACME challenge solver to the issuer. cert-manager will satisfy the
    /// challenge by provisioning a temporary HTTP route at
    /// <c>/.well-known/acme-challenge/{token}</c> on the same hostname being validated.
    /// This requires the hostname to be publicly reachable on port 80.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    /// <remarks>
    /// HTTP-01 is the right choice for gateways exposed via Azure Application Gateway for
    /// Containers (AGC) or any ingress controller that publishes a publicly addressable
    /// hostname. Wildcard certificates require a DNS-01 solver, which is not yet supported.
    /// </remarks>
    [AspireExport(Description = "Adds an HTTP-01 ACME challenge solver to the issuer")]
    public static IResourceBuilder<CertManagerIssuerResource> WithHttp01Solver(
        this IResourceBuilder<CertManagerIssuerResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.Solvers.Add(new CertManagerHttp01SolverConfig());
        return builder;
    }

    /// <summary>
    /// Adds an HTTPS listener to the gateway and wires it to the supplied cert-manager
    /// <c>ClusterIssuer</c>. This adds the <c>cert-manager.io/cluster-issuer</c> annotation
    /// to the generated Gateway resource, causing cert-manager to provision and renew a
    /// certificate for each gateway listener hostname.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="issuer">The cert-manager <c>ClusterIssuer</c> to issue certificates from.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <remarks>
    /// Equivalent to calling <c>WithTls()</c> followed by
    /// <c>WithGatewayAnnotation("cert-manager.io/cluster-issuer", issuer.Resource.Name)</c>,
    /// but type-safe and refactor-friendly. Throws if the gateway and the issuer's
    /// cert-manager installation are not part of the same Kubernetes environment, since
    /// cert-manager is per-cluster and would otherwise silently produce an unsatisfiable
    /// TLS configuration.
    /// </remarks>
    [AspireExport("withGatewayTlsIssuer", Description = "Configures TLS on a Kubernetes Gateway using a cert-manager ClusterIssuer")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<CertManagerIssuerResource> issuer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(issuer);

        var gatewayEnvironment = builder.Resource.Parent;
        var issuerEnvironment = issuer.Resource.Parent.Parent;
        var nameComparer = new ResourceNameComparer();
        if (!nameComparer.Equals(gatewayEnvironment, issuerEnvironment))
        {
            throw new InvalidOperationException(
                $"Gateway '{builder.Resource.Name}' is in Kubernetes environment '{gatewayEnvironment.Name}' but issuer " +
                $"'{issuer.Resource.Name}' belongs to cert-manager installation '{issuer.Resource.Parent.Name}' in environment " +
                $"'{issuerEnvironment.Name}'. cert-manager is per-cluster, so an issuer can only be used by gateways in the " +
                "same Kubernetes environment.");
        }

        return builder
            .WithTls()
            .WithGatewayAnnotation(ClusterIssuerAnnotationKey, issuer.Resource.Name);
    }

    private static Task<IEnumerable<PipelineStep>> BuildIssuerApplySteps(
        CertManagerResource certManager,
        string chartName)
    {
        var steps = new List<PipelineStep>();

        foreach (var issuer in certManager.Issuers)
        {
            // Capture each issuer in its own local so the closure below doesn't see the
            // foreach iteration variable.
            var captured = issuer;

            var step = new PipelineStep
            {
                Name = $"cm-issuer-apply-{captured.Name}",
                Description = $"Applies cert-manager ClusterIssuer '{captured.Name}'",
                Action = ctx => ApplyClusterIssuerAsync(ctx, certManager, captured)
            };

            // Wait for cert-manager itself to be installed and ready (helm install uses
            // --wait, so the validating webhook is guaranteed to be Available before this
            // step runs). Without this dep we'd race the webhook and get
            // 'failed calling webhook "webhook.cert-manager.io": no endpoints available'.
            step.DependsOn($"helm-install-{chartName}");
            step.RequiredBy(WellKnownPipelineSteps.Deploy);
            steps.Add(step);
        }

        return Task.FromResult<IEnumerable<PipelineStep>>(steps);
    }

    private static Task<IEnumerable<PipelineStep>> BuildIssuerDeleteSteps(
        CertManagerResource certManager,
        string chartName)
    {
        var steps = new List<PipelineStep>();

        foreach (var issuer in certManager.Issuers)
        {
            var captured = issuer;

            var step = new PipelineStep
            {
                Name = $"cm-issuer-delete-{captured.Name}",
                Description = $"Deletes cert-manager ClusterIssuer '{captured.Name}'",
                Action = ctx => DeleteClusterIssuerAsync(ctx, certManager, captured),
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq]
            };

            // Run before the cert-manager helm chart is uninstalled. Once the chart goes,
            // the cert-manager.io CRDs are removed and the API server forgets ClusterIssuer
            // exists, so a delete attempted afterwards either silently no-ops (object kind
            // not found) or fails. Deleting while cert-manager is still alive also lets the
            // controller clean up the ACME account secret it created in the cert-manager
            // namespace.
            step.RequiredBy($"helm-uninstall-{chartName}");
            step.RequiredBy(WellKnownPipelineSteps.Destroy);
            steps.Add(step);
        }

        return Task.FromResult<IEnumerable<PipelineStep>>(steps);
    }

    private static async Task ApplyClusterIssuerAsync(
        PipelineStepContext context,
        CertManagerResource certManager,
        CertManagerIssuerResource issuer)
    {
        if (issuer.Spec is null)
        {
            throw new InvalidOperationException(
                $"ClusterIssuer '{issuer.Name}' has no spec. Configure it with WithLetsEncryptProduction(), " +
                "WithLetsEncryptStaging(), or WithAcmeServer().");
        }

        if (issuer.Solvers.Count == 0)
        {
            throw new InvalidOperationException(
                $"ClusterIssuer '{issuer.Name}' has no solvers configured. Add at least one " +
                "solver via WithHttp01Solver().");
        }

        var environment = certManager.Parent;

        var manifest = await BuildClusterIssuerManifestAsync(context.Model, certManager, issuer, context.Logger, context.CancellationToken)
            .ConfigureAwait(false);

        context.Logger.LogInformation(
            "Applying cert-manager ClusterIssuer '{IssuerName}'.", issuer.Name);

        // Write to a temp file and apply. kubectl apply -f - via stdin would avoid the
        // temp file but ProcessUtil.Run doesn't expose a stdin pipe; Directory.CreateTempSubdirectory
        // is the standard temp pattern in this codebase (see EnsureBootstrapTlsSecretAsync).
        var tempDir = Directory.CreateTempSubdirectory(".aspire-cm-issuer");
        try
        {
            var manifestPath = Path.Combine(tempDir.FullName, $"{issuer.Name}.yaml");
            await File.WriteAllTextAsync(manifestPath, manifest, context.CancellationToken).ConfigureAwait(false);

            var args = new StringBuilder();
            args.Append(CultureInfo.InvariantCulture, $"apply -f \"{manifestPath}\"");
            if (environment.KubeConfigPath is not null)
            {
                args.Append(CultureInfo.InvariantCulture, $" --kubeconfig \"{environment.KubeConfigPath}\"");
            }

            var stderr = new StringBuilder();
            var (resultTask, disposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
            {
                Arguments = args.ToString(),
                ThrowOnNonZeroReturnCode = false,
                InheritEnv = true,
                OnOutputData = line => context.Logger.LogDebug("kubectl: {Line}", line),
                OnErrorData = line =>
                {
                    stderr.AppendLine(line);
                    context.Logger.LogDebug("kubectl: {Line}", line);
                }
            });

            await using (disposable.ConfigureAwait(false))
            {
                var result = await resultTask.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    var errOut = stderr.ToString().Trim();
                    throw new InvalidOperationException(
                        $"kubectl apply for ClusterIssuer '{issuer.Name}' failed with exit code {result.ExitCode}: {errOut}");
                }
            }

            context.Logger.LogInformation("ClusterIssuer '{IssuerName}' applied.", issuer.Name);
        }
        finally
        {
            try { tempDir.Delete(recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    private static async Task DeleteClusterIssuerAsync(
        PipelineStepContext context,
        CertManagerResource certManager,
        CertManagerIssuerResource issuer)
    {
        var environment = certManager.Parent;
        // Match the lowercase normalization used at apply time so we target the same object.
        var k8sIssuerName = issuer.Name.ToKubernetesResourceName();

        context.Logger.LogInformation(
            "Deleting cert-manager ClusterIssuer '{IssuerName}'.", issuer.Name);

        var args = new StringBuilder();
        // --ignore-not-found makes destroy idempotent: re-running aspire destroy after a
        // partially-completed prior destroy (or after the issuer was deleted manually) is a
        // no-op rather than a failure.
        args.Append(CultureInfo.InvariantCulture, $"delete clusterissuer {k8sIssuerName} --ignore-not-found");
        if (environment.KubeConfigPath is not null)
        {
            args.Append(CultureInfo.InvariantCulture, $" --kubeconfig \"{environment.KubeConfigPath}\"");
        }

        var stderr = new StringBuilder();
        var (resultTask, disposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
        {
            Arguments = args.ToString(),
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = line => context.Logger.LogDebug("kubectl: {Line}", line),
            OnErrorData = line =>
            {
                stderr.AppendLine(line);
                context.Logger.LogDebug("kubectl: {Line}", line);
            }
        });

        await using (disposable.ConfigureAwait(false))
        {
            var result = await resultTask.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                // Don't fail destroy if delete encountered an error — log and continue so the
                // rest of the destroy pipeline (helm-uninstall, infra teardown) can still run.
                // The cluster is most likely about to be torn down anyway.
                context.Logger.LogWarning(
                    "kubectl delete for ClusterIssuer '{IssuerName}' returned exit code {ExitCode}: {StdErr}",
                    issuer.Name,
                    result.ExitCode,
                    stderr.ToString().Trim());
                return;
            }
        }

        context.Logger.LogInformation("ClusterIssuer '{IssuerName}' deleted.", issuer.Name);
    }

    internal static async Task<string> BuildClusterIssuerManifestAsync(
        ApplicationModel.DistributedApplicationModel model,
        CertManagerResource certManager,
        CertManagerIssuerResource issuer,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("apiVersion: cert-manager.io/v1");
        sb.AppendLine("kind: ClusterIssuer");
        sb.AppendLine("metadata:");
        // Kubernetes object metadata.name must be a valid DNS-1123 subdomain (lowercase).
        // Aspire resource names allow uppercase, so normalise here to match the same rule the
        // rest of the Kubernetes integration uses for generated object names.
        var k8sIssuerName = issuer.Name.ToKubernetesResourceName();
        sb.Append("  name: ").AppendLine(k8sIssuerName);
        sb.AppendLine("spec:");

        switch (issuer.Spec)
        {
            case CertManagerAcmeIssuerSpec acme:
                {
                    var server = await acme.ServerUrl.GetValueAsync(cancellationToken).ConfigureAwait(false);
                    var email = await acme.Email.GetValueAsync(cancellationToken).ConfigureAwait(false);
                    sb.AppendLine("  acme:");
                    sb.Append("    server: ").AppendLine(server);
                    sb.Append("    email: ").AppendLine(email);
                    sb.AppendLine("    privateKeySecretRef:");
                    sb.Append("      name: ").Append(k8sIssuerName).AppendLine("-account-key");
                    sb.AppendLine("    solvers:");
                    foreach (var solver in issuer.Solvers)
                    {
                        AppendSolver(sb, solver, model, certManager, issuer, logger);
                    }
                    break;
                }
            default:
                throw new InvalidOperationException(
                    $"Unknown issuer spec type '{issuer.Spec?.GetType().Name}' on issuer '{issuer.Name}'.");
        }

        return sb.ToString();
    }

    private static void AppendSolver(
        StringBuilder sb,
        CertManagerSolverConfig solver,
        ApplicationModel.DistributedApplicationModel model,
        CertManagerResource certManager,
        CertManagerIssuerResource issuer,
        ILogger logger)
    {
        switch (solver)
        {
            case CertManagerHttp01SolverConfig:
                {
                    sb.AppendLine("    - http01:");
                    sb.AppendLine("        gatewayHTTPRoute:");

                    // cert-manager's HTTP-01 Gateway API solver creates an HTTPRoute that has to
                    // attach to the Gateway being validated. Without parentRefs, the route is
                    // orphaned and the ACME challenge URL is unreachable.
                    // See https://cert-manager.io/docs/configuration/acme/http01/#configuring-the-http01-gateway-api-solver
                    var nameComparer = new ResourceNameComparer();
                    var parentGateways = model.Resources
                        .OfType<KubernetesGatewayResource>()
                        .Where(g => nameComparer.Equals(g.Parent, certManager.Parent)
                                    && g.GatewayAnnotations.TryGetValue(ClusterIssuerAnnotationKey, out var v)
                                    && string.Equals(v.Format, issuer.Name, StringComparison.Ordinal))
                        .ToList();

                    if (parentGateways.Count == 0)
                    {
                        // No annotated gateway found. cert-manager will accept this manifest but
                        // the HTTP-01 challenge can never be satisfied because there's no parent
                        // Gateway for the solver's HTTPRoute to attach to. Emit a warning so the
                        // misconfiguration is visible at deploy time instead of leaving the user
                        // to discover it via Certificates stuck in 'Pending' indefinitely.
                        logger.LogWarning(
                            "ClusterIssuer '{IssuerName}' has an HTTP-01 solver but no Gateway in environment '{EnvironmentName}' is annotated with " +
                            ClusterIssuerAnnotationKey + "={IssuerName}. cert-manager will not be able to satisfy ACME challenges until at least " +
                            "one Gateway adopts this issuer (e.g. via WithTls(issuer)).",
                            issuer.Name,
                            certManager.Parent.Name,
                            issuer.Name);
                        return;
                    }

                    sb.AppendLine("          parentRefs:");
                    foreach (var gateway in parentGateways)
                    {
                        sb.AppendLine("            - group: gateway.networking.k8s.io");
                        sb.AppendLine("              kind: Gateway");
                        // Match the metadata.name normalization used by BuildGatewayObjects so
                        // the parentRef resolves to the actual Gateway in the cluster.
                        sb.Append("              name: ").AppendLine(gateway.Name.ToKubernetesResourceName());
                    }
                    break;
                }
            default:
                throw new InvalidOperationException(
                    $"Unknown solver type '{solver.GetType().Name}' on issuer '{issuer.Name}'.");
        }
    }
}
