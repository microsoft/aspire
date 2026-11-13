// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
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
    /// <ats-summary>Installs cert-manager into a Kubernetes environment</ats-summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The Aspire resource name for the cert-manager installation. Each call
    /// adds a uniquely-named resource to the application model, so multiple Kubernetes environments
    /// must each pass distinct names.</param>
    /// <param name="chartVersion">The cert-manager Helm chart version to install.
    /// Defaults to a pinned version validated against this Aspire build.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
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
    /// <ats-remarks />
    [AspireExport]
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

        var resourceBuilder = builder.ApplicationBuilder.AddResource(resource).WithIconName("Certificate").ExcludeFromManifest();

        // Emit one kubectl-apply step per ClusterIssuer at deploy time, plus one kubectl-delete
        // step per ClusterIssuer at destroy time. The annotation factory closures capture the
        // CertManagerResource and read .Issuers when the pipeline is assembled, so issuers added
        // via AddIssuer(...) after this call are still picked up.
        // Apply steps depend on helm-install-{chartName} so cert-manager (and its admission
        // webhook) is up before we apply CRD instances. Delete steps run before
        // helm-uninstall-{chartName} so issuer and managed EAB Secret cleanup runs while
        // the controller is still alive. ACME account keys are retained.
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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

        return builder.ApplicationBuilder.AddResource(issuer).WithIconName("ContactCard").ExcludeFromManifest();
    }

    /// <summary>
    /// Configures the issuer to use an ACME directory endpoint, such as Let's Encrypt,
    /// ZeroSSL, or a private ACME server.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="serverUrl">The ACME directory URL (e.g., <c>https://acme.example.com/directory</c>).</param>
    /// <param name="email">The contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Handled by the withAcmeServer union export.")]
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
    /// Configures External Account Binding (EAB) with a key identifier and secret HMAC key resolved at deploy time.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="keyId">A parameter containing the key identifier supplied by the ACME provider.</param>
    /// <param name="hmacKey">A secret parameter containing the provider's base64url-encoded HMAC key.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Aspire applies an issuer-specific Kubernetes Secret in cert-manager's cluster resource namespace
    /// at deploy time and attempts to delete it on destroy. The HMAC key is not included in published artifacts.
    /// EAB authorizes account registration, not domain ownership. Preserve cert-manager's separate account-key Secret.
    /// See <see href="https://www.rfc-editor.org/rfc/rfc8555.html#section-7.3.4"/> and
    /// <see href="https://cert-manager.io/docs/configuration/acme/#external-account-bindings"/>.
    /// Supply fresh credential pairs from a deployment pipeline when registering a new account with
    /// a provider that uses expiring or single-use EAB credentials. Aspire does not acquire or refresh
    /// provider credentials. Existing ACME accounts are reused by cert-manager.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The HMAC parameter is not secret.</exception>
    [AspireExport]
    public static IResourceBuilder<CertManagerIssuerResource> WithExternalAccountBinding(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        IResourceBuilder<ParameterResource> keyId,
        IResourceBuilder<ParameterResource> hmacKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(hmacKey);

        if (!hmacKey.Resource.Secret)
        {
            throw new ArgumentException("The EAB HMAC key must be a secret parameter.", nameof(hmacKey));
        }

        builder.Resource.ExternalAccountBinding = new CertManagerExternalAccountBinding(ReferenceExpression.Create($"{keyId.Resource}"), hmacKey.Resource);
        return builder;
    }

    /// <summary>
    /// Configures the issuer with an ACME directory URL and contact email resolved from parameters at deploy time.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="serverUrl">A parameter containing the ACME directory URL.</param>
    /// <param name="email">A parameter containing the contact email registered with the ACME account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    [AspireExportIgnore(Reason = "Handled by the withAcmeServer union export.")]
    public static IResourceBuilder<CertManagerIssuerResource> WithAcmeServer(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        IResourceBuilder<ParameterResource> serverUrl,
        IResourceBuilder<ParameterResource> email)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serverUrl);
        ArgumentNullException.ThrowIfNull(email);

        builder.Resource.Spec = new CertManagerAcmeIssuerSpec(
            ReferenceExpression.Create($"{serverUrl.Resource}"),
            ReferenceExpression.Create($"{email.Resource}"));

        return builder;
    }

    /// <summary>
    /// Configures the ACME directory URL and contact email using either two strings or two parameters.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <param name="serverUrl">The ACME directory URL, as a string or parameter resource builder.</param>
    /// <param name="email">The account contact email, as a string or parameter resource builder.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport("withAcmeServer")]
    internal static IResourceBuilder<CertManagerIssuerResource> WithAcmeServerForPolyglot(
        this IResourceBuilder<CertManagerIssuerResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object serverUrl,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object email)
        => (serverUrl, email) switch
        {
            (string url, string contact) => builder.WithAcmeServer(url, contact),
            (IResourceBuilder<ParameterResource> url, IResourceBuilder<ParameterResource> contact) => builder.WithAcmeServer(url, contact),
            _ => throw new ArgumentException("Server URL and email must both be strings or both be parameter resource builders.")
        };

    /// <summary>
    /// Adds an HTTP-01 ACME challenge solver to the issuer. cert-manager will satisfy the
    /// challenge by provisioning a temporary HTTP route at
    /// <c>/.well-known/acme-challenge/{token}</c> on the same hostname being validated.
    /// This requires the hostname to be publicly reachable on port 80.
    /// </summary>
    /// <param name="builder">The issuer resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{CertManagerIssuerResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// HTTP-01 is the right choice for gateways exposed via Azure Application Gateway for
    /// Containers (AGC) or any ingress controller that publishes a publicly addressable
    /// hostname. Wildcard certificates require a DNS-01 solver, which is not yet supported.
    /// </remarks>
    [AspireExport]
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
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Equivalent to calling <c>WithTls()</c> followed by
    /// <c>WithGatewayAnnotation("cert-manager.io/cluster-issuer", issuer.Resource.Name)</c>,
    /// but type-safe and refactor-friendly. Throws if the gateway and the issuer's
    /// cert-manager installation are not part of the same Kubernetes environment, since
    /// cert-manager is per-cluster and would otherwise silently produce an unsatisfiable
    /// TLS configuration.
    /// </remarks>
    [AspireExport("withGatewayTlsIssuer")]
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
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq],
                Tags = [HelmDeploymentEngine.GetKubernetesDestroyTag(certManager.Parent.Name)]
            };

            // Run while cert-manager and its CRDs are still available for issuer cleanup.
            // Aspire deletes its EAB Secret but preserves the separate ACME account key
            // managed by cert-manager.
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
                $"ClusterIssuer '{issuer.Name}' has no spec. Configure it with WithAcmeServer().");
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

        if (issuer.ExternalAccountBinding is not null)
        {
            var resourceVersion = await GetExternalAccountBindingSecretResourceVersionAsync(issuer, context.CancellationToken).ConfigureAwait(false);
            var secretSpec = await BuildExternalAccountBindingSecretApplySpecAsync(issuer, resourceVersion, context.CancellationToken).ConfigureAwait(false);
            var (secretResult, secretDisposable) = ProcessUtil.Run(secretSpec);
            await using (secretDisposable.ConfigureAwait(false))
            {
                var result = await secretResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    // kubectl errors can echo the complete Secret, including its encoded data.
                    // Report the failed operation without retaining or logging either output stream.
                    throw new InvalidOperationException(
                        $"Applying the EAB Secret for ClusterIssuer '{issuer.Name}' failed with exit code {result.ExitCode}. " +
                        "Check the Kubernetes context, namespace, permissions, and Secret ownership.");
                }
            }
        }

        context.Logger.LogInformation(
            "Applying cert-manager ClusterIssuer '{IssuerName}'.", issuer.Name);

        var arguments = new List<string> { "apply", "-f", "-" };
        AddKubeConfigArgument(arguments, environment);
        var stderr = new StringBuilder();
        var (resultTask, disposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
        {
            ArgumentList = arguments,
            StandardInputContent = manifest,
            ThrowOnNonZeroReturnCode = false,
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

    private static async Task DeleteClusterIssuerAsync(
        PipelineStepContext context,
        CertManagerResource certManager,
        CertManagerIssuerResource issuer)
    {
        var environment = certManager.Parent;
        if (environment.SkipDestroyCleanup)
        {
            context.Logger.LogInformation(
                "Skipping cert-manager cleanup for Kubernetes environment '{EnvironmentName}' because the cluster no longer exists.",
                environment.Name);
            return;
        }

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
            }
            else
            {
                context.Logger.LogInformation("ClusterIssuer '{IssuerName}' deleted.", issuer.Name);
            }
        }

        // Clean up even after a failed issuer deletion or removal of EAB from the model.
        await DeleteExternalAccountBindingSecretAsync(context, issuer).ConfigureAwait(false);
    }

    internal static async Task DeleteExternalAccountBindingSecretAsync(
        PipelineStepContext context, CertManagerIssuerResource issuer)
    {
        var certManager = issuer.Parent;
        var environment = certManager.Parent;
        var secretName = GetExternalAccountBindingSecretName(issuer);
        string @namespace;
        try
        {
            @namespace = GetClusterResourceNamespace(certManager, validateEabConfiguration: issuer.ExternalAccountBinding is not null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            context.Logger.LogWarning(
                "Could not determine the namespace for EAB Secret '{SecretName}': {Error}. Remove the Secret manually from cert-manager's cluster resource namespace. Continuing destroy.",
                secretName, ex.Message);
            return;
        }
        var arguments = new List<string>
        {
            "delete", "secrets",
            "--field-selector", $"metadata.name={secretName}",
            "--selector", "app.kubernetes.io/managed-by=aspire-cert-manager",
            "--namespace", @namespace, "--ignore-not-found"
        };
        AddKubeConfigArgument(arguments, environment);
        var (deleteResult, deleteDisposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
        {
            ArgumentList = arguments,
            ThrowOnNonZeroReturnCode = false
        });
        await using (deleteDisposable.ConfigureAwait(false))
        {
            var result = await deleteResult.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                context.Logger.LogWarning(
                    "Deleting EAB Secret '{SecretName}' in namespace '{Namespace}' failed with exit code {ExitCode}. Remove the Secret manually. Continuing destroy.",
                    secretName, @namespace, result.ExitCode);
            }
        }
    }

    internal static async Task<ProcessSpec> BuildExternalAccountBindingSecretApplySpecAsync(
        CertManagerIssuerResource issuer,
        string? resourceVersion,
        CancellationToken cancellationToken)
    {
        var binding = issuer.ExternalAccountBinding
            ?? throw new InvalidOperationException($"ClusterIssuer '{issuer.Name}' has no External Account Binding.");
        var hmacKey = await binding.HmacKey.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(hmacKey))
        {
            throw new InvalidOperationException($"The EAB HMAC parameter for ClusterIssuer '{issuer.Name}' has no value.");
        }

        // Secret.data is base64 of the provider's base64url text, not of the decoded MAC key.
        // See https://cert-manager.io/docs/configuration/acme/#external-account-bindings.
        var metadata = new Dictionary<string, object>
        {
            ["name"] = GetExternalAccountBindingSecretName(issuer),
            ["namespace"] = GetClusterResourceNamespace(issuer.Parent, validateEabConfiguration: true),
            ["labels"] = new Dictionary<string, string>
            {
                ["app.kubernetes.io/managed-by"] = "aspire-cert-manager"
            }
        };
        if (resourceVersion is not null)
        {
            metadata["resourceVersion"] = resourceVersion;
        }
        var manifest = JsonSerializer.Serialize(new
        {
            apiVersion = "v1",
            kind = "Secret",
            metadata,
            type = "Opaque",
            data = new Dictionary<string, string>
            {
                ["hmac-key"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(hmacKey))
            }
        });
        // Create cannot adopt an object that appeared after our read. Replace checks the
        // resourceVersion, rejecting an object changed or recreated since ownership was checked.
        var arguments = new List<string> { resourceVersion is null ? "create" : "replace", "-f", "-" };
        AddKubeConfigArgument(arguments, issuer.Parent.Parent);

        return new ProcessSpec("kubectl")
        {
            ArgumentList = arguments,
            StandardInputContent = manifest,
            ThrowOnNonZeroReturnCode = false,
            RetainedOutputLineCount = 0
        };
    }

    private static async Task<string?> GetExternalAccountBindingSecretResourceVersionAsync(
        CertManagerIssuerResource issuer,
        CancellationToken cancellationToken)
    {
        var secretName = GetExternalAccountBindingSecretName(issuer);
        var arguments = new List<string>
        {
            "get", "secret", secretName, "--namespace", GetClusterResourceNamespace(issuer.Parent, validateEabConfiguration: true),
            "--ignore-not-found", "-o", @"jsonpath={.metadata.name}:{.metadata.labels.app\.kubernetes\.io/managed-by}:{.metadata.resourceVersion}"
        };
        AddKubeConfigArgument(arguments, issuer.Parent.Parent);
        var output = new StringBuilder();
        var (resultTask, disposable) = ProcessUtil.Run(new ProcessSpec("kubectl")
        {
            ArgumentList = arguments,
            ThrowOnNonZeroReturnCode = false,
            OnOutputData = line => output.Append(line)
        });
        await using (disposable.ConfigureAwait(false))
        {
            var result = await resultTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Checking ownership of EAB Secret '{secretName}' failed with exit code {result.ExitCode}.");
            }
        }

        return ParseExternalAccountBindingSecretResourceVersion(secretName, output.ToString());
    }

    internal static string? ParseExternalAccountBindingSecretResourceVersion(string secretName, string output)
    {
        // jsonpath returns "issuer-eab:aspire-cert-manager:12345", or no output if absent.
        if (output.Length == 0)
        {
            return null;
        }
        var separator = output.LastIndexOf(':');
        if (separator < 0 || separator == output.Length - 1)
        {
            throw new InvalidOperationException($"EAB Secret '{secretName}' has no resource version.");
        }
        ValidateExternalAccountBindingSecretOwnership(secretName, output[..separator]);
        return output[(separator + 1)..];
    }

    internal static void ValidateExternalAccountBindingSecretOwnership(string secretName, string output)
    {
        // jsonpath returns "issuer-eab:aspire-cert-manager" for an owned Secret and
        // "issuer-eab:" for an unlabelled one. --ignore-not-found returns no output.
        if (output.Length > 0 && output != $"{secretName}:aspire-cert-manager")
        {
            throw new InvalidOperationException(
                $"EAB Secret '{secretName}' already exists and is not managed by Aspire cert-manager. " +
                "Use a different issuer name rather than overwriting or deleting this Secret.");
        }
    }

    internal static string GetClusterResourceNamespace(CertManagerResource certManager, bool validateEabConfiguration)
    {
        var chart = certManager.HelmChart;
        if (validateEabConfiguration && chart.Values.Any(pair => pair.Key.StartsWith("extraArgs", StringComparison.Ordinal)
            && pair.Value.Contains("--cluster-resource-namespace", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Configure cert-manager's cluster resource namespace using the 'clusterResourceNamespace' Helm value, " +
                "not 'extraArgs', so Aspire can place EAB Secrets in the same namespace.");
        }

        // The chart's command-line flag takes precedence over controller config and defaults
        // to POD_NAMESPACE. See the upstream chart's templates/deployment.yaml.
        var @namespace = chart.Values.GetValueOrDefault("clusterResourceNamespace");
        if (string.IsNullOrEmpty(@namespace))
        {
            var chartNamespace = chart.Values.GetValueOrDefault("namespace");
            @namespace = string.IsNullOrEmpty(chartNamespace) ? chart.Namespace ?? chart.Name : chartNamespace;
        }

        HelmChartOptions.ValidateNamespace(@namespace, nameof(KubernetesHelmChartResource.Namespace));
        return @namespace;
    }

    private static string GetExternalAccountBindingSecretName(CertManagerIssuerResource issuer)
        => $"{issuer.Name.ToKubernetesResourceName()}-eab";

    private static void AddKubeConfigArgument(List<string> arguments, KubernetesEnvironmentResource environment)
    {
        if (environment.KubeConfigPath is not null)
        {
            arguments.Add("--kubeconfig");
            arguments.Add(environment.KubeConfigPath);
        }
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
                    // Parameter values must stay scalars, even when they contain YAML syntax.
                    sb.Append("    server: ").AppendLine(JsonSerializer.Serialize(server));
                    sb.Append("    email: ").AppendLine(JsonSerializer.Serialize(email));
                    sb.AppendLine("    privateKeySecretRef:");
                    sb.Append("      name: ").Append(k8sIssuerName).AppendLine("-account-key");
                    if (issuer.ExternalAccountBinding is { } binding)
                    {
                        var keyId = await binding.KeyId.GetValueAsync(cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(keyId) || !keyId.All(char.IsAscii))
                        {
                            throw new InvalidOperationException(
                                $"The EAB key identifier for ClusterIssuer '{issuer.Name}' must be a nonempty ASCII string.");
                        }

                        sb.AppendLine("    externalAccountBinding:");
                        // JSON string escaping is also valid for YAML double-quoted scalars.
                        sb.Append("      keyID: ").AppendLine(JsonSerializer.Serialize(keyId));
                        sb.AppendLine("      keySecretRef:");
                        sb.Append("        name: ").AppendLine(GetExternalAccountBindingSecretName(issuer));
                        sb.AppendLine("        key: hmac-key");
                    }
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

                    // cert-manager's HTTP-01 Gateway API solver creates an HTTPRoute that has to
                    // attach to the Gateway being validated. Without parentRefs, the route is
                    // orphaned and the ACME challenge URL is unreachable.
                    // See https://cert-manager.io/docs/configuration/acme/http01/#configuring-the-http01-gateway-api-solver
                    // Route-less gateways are excluded because they are skipped during
                    // materialization: a parentRef to a Gateway that is never created is just as
                    // orphaned, and it would also mask the warning below.
                    var nameComparer = new ResourceNameComparer();
                    var parentGateways = model.Resources
                        .OfType<KubernetesGatewayResource>()
                        .Where(g => nameComparer.Equals(g.Parent, certManager.Parent)
                                    && g.ShouldMaterialize
                                    && g.GatewayAnnotations.TryGetValue(ClusterIssuerAnnotationKey, out var v)
                                    && string.Equals(v.Format, issuer.Name, StringComparison.Ordinal))
                        .ToList();

                    if (parentGateways.Count == 0)
                    {
                        // A bare YAML key is null, not an empty solver object. Account-only
                        // issuers still need a valid solver configuration to pass admission.
                        sb.AppendLine("        gatewayHTTPRoute: {}");
                        // No eligible gateway found — either none is annotated for this issuer, or
                        // the annotated ones have no routes and are therefore skipped during
                        // materialization. Either way cert-manager will accept this manifest but the
                        // HTTP-01 challenge can never be satisfied, because there's no parent Gateway
                        // for the solver's HTTPRoute to attach to. Emit a warning so the
                        // misconfiguration is visible at deploy time instead of leaving the user
                        // to discover it via Certificates stuck in 'Pending' indefinitely.
                        logger.LogWarning(
                            "ClusterIssuer '{IssuerName}' has an HTTP-01 solver but no Gateway in environment '{EnvironmentName}' is both annotated with " +
                            ClusterIssuerAnnotationKey + "={IssuerName} and configured with at least one route. cert-manager will not be able to satisfy " +
                            "ACME challenges until at least one routed Gateway adopts this issuer (e.g. via WithRoute(...) and WithTls(issuer)).",
                            issuer.Name,
                            certManager.Parent.Name,
                            issuer.Name);
                        return;
                    }

                    sb.AppendLine("        gatewayHTTPRoute:");
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
