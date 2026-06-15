// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // IDeploymentStateManager is for evaluation purposes only.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding and configuring external Helm charts
/// in a Kubernetes environment.
/// </summary>
public static partial class KubernetesHelmChartExtensions
{
    /// <summary>
    /// Adds an external Helm chart to be installed in the Kubernetes environment.
    /// The chart is installed via <c>helm upgrade --install</c> as a pipeline step
    /// after the main application Helm chart is deployed.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the Helm chart resource (used as release name and namespace if not overridden).</param>
    /// <param name="chartReference">
    /// The Helm chart reference. Can be an OCI registry URL (e.g., <c>oci://quay.io/jetstack/charts/cert-manager</c>)
    /// or a chart name from an added repository.
    /// </param>
    /// <param name="chartVersion">The chart version to install.</param>
    /// <returns>A resource builder for the Helm chart resource.</returns>
    /// <remarks>
    /// <para>
    /// The chart is installed in a dedicated namespace (defaulting to the chart resource name).
    /// Use <see cref="WithNamespace"/> to override the namespace, and <see cref="WithHelmValue"/>
    /// to set chart values.
    /// </para>
    /// <para>
    /// By default, <c>aspire destroy</c> does <em>not</em> uninstall the external chart, because
    /// it may be shared with workloads outside the Aspire app. Opt in to destroy-time uninstall
    /// by chaining <see cref="WithDestroy"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var k8s = builder.AddKubernetesEnvironment("k8s");
    ///
    /// // Install cert-manager from OCI registry
    /// k8s.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0")
    ///     .WithHelmValue("crds.enabled", "true");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<KubernetesHelmChartResource> AddHelmChart(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name,
        string chartReference,
        string chartVersion)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(chartReference);
        ArgumentException.ThrowIfNullOrEmpty(chartVersion);

        ValidateChartReference(chartReference, nameof(chartReference));
        HelmChartOptions.ValidateChartVersion(chartVersion, nameof(chartVersion));

        var environment = builder.Resource;
        var resource = new KubernetesHelmChartResource(name, environment, chartReference, chartVersion);

        // Helm chart installation is a publish/deploy-time concern only. In run mode the
        // parent KubernetesEnvironmentResource isn't added to the model (see
        // AddKubernetesEnvironment), so any helm-install step that depends on
        // helm-deploy-{env.Name} would fail step validation with a missing-dependency error.
        // Mirror AddIngress/AddGateway and skip model registration entirely in run mode.
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(resource);
        }

        var chartBuilder = builder.ApplicationBuilder.AddResource(resource);

        chartBuilder.WithAnnotation(new PipelineStepAnnotation(_ =>
        {
            // Resolve and validate release/namespace at step-creation time so the user
            // gets a clear error before the helm CLI ever runs.
            var (releaseName, @namespace) = ResolveReleaseAndNamespace(resource);

            var steps = new List<PipelineStep>();

            var installStep = new PipelineStep
            {
                Name = $"helm-install-{name}",
                Description = $"Installs Helm chart '{name}' ({resource.ChartReference}:{resource.ChartVersion})",
                Action = ctx => InstallHelmChartAsync(ctx, environment, resource, releaseName, @namespace)
            };

            installStep.DependsOn($"helm-deploy-{environment.Name}");
            installStep.RequiredBy(WellKnownPipelineSteps.Deploy);
            steps.Add(installStep);

            if (resource.DestroyOnUninstall)
            {
                var uninstallStep = new PipelineStep
                {
                    Name = $"helm-uninstall-{name}",
                    Description = $"Uninstalls Helm chart '{name}' from namespace '{@namespace}'",
                    Action = ctx => UninstallHelmChartAsync(ctx, environment, resource, releaseName, @namespace),
                    DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq]
                };
                // The uninstall path shells out to `helm uninstall`, so it must observe the same
                // Helm CLI / version preflight as the deploy path. Without this dep, a missing or
                // too-old Helm during teardown would surface as the raw spawn / unknown-flag error
                // the env-wide `check-helm-prereqs-{env}` step exists to convert into an actionable
                // message. Install is already covered transitively via `helm-deploy-{env}`.
                uninstallStep.DependsOn($"check-helm-prereqs-{environment.Name}");
                uninstallStep.RequiredBy(WellKnownPipelineSteps.Destroy);
                steps.Add(uninstallStep);
            }

            return Task.FromResult<IEnumerable<PipelineStep>>(steps);
        }));

        return chartBuilder;
    }

    /// <summary>
    /// Sets a Helm value for the chart installation. Values are passed to <c>helm upgrade --install</c>
    /// via <c>--set</c> flags.
    /// </summary>
    /// <param name="builder">The Helm chart resource builder.</param>
    /// <param name="key">The value key using dot notation (e.g., <c>config.enableGatewayAPI</c>).</param>
    /// <param name="value">The value to set.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<KubernetesHelmChartResource> WithHelmValue(
        this IResourceBuilder<KubernetesHelmChartResource> builder,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        ValidateHelmSetKey(key, nameof(key));
        ValidateHelmSetValue(value, nameof(value));

        builder.Resource.Values[key] = value;
        return builder;
    }

    /// <summary>
    /// Sets the Kubernetes namespace for the Helm chart installation.
    /// If not set, the namespace defaults to the chart resource name.
    /// </summary>
    /// <param name="builder">The Helm chart resource builder.</param>
    /// <param name="namespace">The namespace to install the chart into.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExport("withHelmChartNamespace")]
    public static IResourceBuilder<KubernetesHelmChartResource> WithNamespace(
        this IResourceBuilder<KubernetesHelmChartResource> builder,
        string @namespace)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(@namespace);

        HelmChartOptions.ValidateNamespace(@namespace, nameof(@namespace));

        builder.Resource.Namespace = @namespace;
        return builder;
    }

    /// <summary>
    /// Sets the Helm release name for the chart installation.
    /// If not set, the release name defaults to the resource name.
    /// </summary>
    /// <param name="builder">The Helm chart resource builder.</param>
    /// <param name="releaseName">The Helm release name.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExport("withHelmChartReleaseName")]
    public static IResourceBuilder<KubernetesHelmChartResource> WithReleaseName(
        this IResourceBuilder<KubernetesHelmChartResource> builder,
        string releaseName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(releaseName);

        HelmChartOptions.ValidateReleaseName(releaseName, nameof(releaseName));

        builder.Resource.ReleaseName = releaseName;
        return builder;
    }

    /// <summary>
    /// Opts the Helm chart in to destroy-time uninstall. When set, <c>aspire destroy</c>
    /// will run <c>helm uninstall</c> for this release as part of the destroy pipeline.
    /// </summary>
    /// <param name="builder">The Helm chart resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// External Helm charts are not uninstalled by default because they may be shared
    /// with workloads outside the Aspire app (for example, cert-manager or an ingress
    /// controller installed once for many apps). Opt in only when the chart's lifecycle
    /// is owned by this app.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// k8s.AddHelmChart("podinfo", "oci://ghcr.io/stefanprodan/charts/podinfo", "6.7.1")
    ///     .WithDestroy();
    /// </code>
    /// </example>
    [AspireExport("withHelmChartDestroy")]
    public static IResourceBuilder<KubernetesHelmChartResource> WithDestroy(
        this IResourceBuilder<KubernetesHelmChartResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.DestroyOnUninstall = true;
        return builder;
    }

    /// <summary>
    /// Opts the Helm chart in to <c>helm upgrade --install --force-conflicts</c>. When set,
    /// Helm's server-side apply forcibly takes over any fields owned by another field
    /// manager instead of failing with a conflict.
    /// </summary>
    /// <param name="builder">The Helm chart resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This is most commonly needed for charts whose templates ship admission webhooks
    /// (cert-manager, kyverno, gatekeeper, opa-gatekeeper, etc.) on clusters where another
    /// admission controller — such as the AKS <c>admissionsenforcer</c> field manager
    /// installed by the Azure Policy add-on or Deployment Safeguards — mutates the webhook
    /// configuration after install. Helm's Server-Side Apply (used by default for charts
    /// that opt in, including cert-manager) refuses to overwrite fields owned by another
    /// field manager. Without <c>--force-conflicts</c>, the next <c>helm upgrade</c> fails
    /// with a "conflict with admissionsenforcer" error on the webhook's
    /// <c>namespaceSelector</c> (or similar).
    /// See
    /// <see href="https://learn.microsoft.com/azure/aks/deployment-safeguards">Deployment Safeguards in AKS</see>
    /// and
    /// <see href="https://kubernetes.io/docs/reference/using-api/server-side-apply/#conflicts">Server-Side Apply conflicts</see>
    /// for background.
    /// </para>
    /// <para>
    /// Unlike the deprecated <c>--force</c> / <c>--force-replace</c> (which delete and
    /// recreate the resource and are incompatible with Server-Side Apply),
    /// <c>--force-conflicts</c> is non-destructive — it only changes which field manager
    /// owns the conflicting field. No resources are deleted or recreated. This flag is
    /// also distinct from Helm's <c>--take-ownership</c>, which transfers ownership of an
    /// entire resource between Helm releases and does not address field-level conflicts.
    /// </para>
    /// <para>
    /// Requires Helm v3.18 or later (the version that introduced <c>--force-conflicts</c>
    /// for <c>helm upgrade --install</c>). Older Helm versions fail with
    /// <c>Error: unknown flag: --force-conflicts</c>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // cert-manager on AKS clusters with Azure Policy / Deployment Safeguards enabled.
    /// k8s.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "v1.18.2")
    ///     .WithHelmValue("crds.enabled", "true")
    ///     .WithForceConflicts();
    /// </code>
    /// </example>
    [AspireExport("withHelmChartForceConflicts")]
    public static IResourceBuilder<KubernetesHelmChartResource> WithForceConflicts(
        this IResourceBuilder<KubernetesHelmChartResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ForceConflicts = true;
        return builder;
    }

    private static async Task InstallHelmChartAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment,
        KubernetesHelmChartResource chart,
        string releaseName,
        string @namespace)
    {
        var logger = context.Services.GetRequiredService<ILogger<KubernetesHelmChartResource>>();
        var helmRunner = context.Services.GetRequiredService<IHelmRunner>();

        logger.LogInformation(
            "Installing Helm chart '{ChartName}' ({ChartRef}:{ChartVersion}) into namespace '{Namespace}'.",
            chart.Name, chart.ChartReference, chart.ChartVersion, @namespace);

        var arguments = new StringBuilder();
        arguments.Append(CultureInfo.InvariantCulture, $"upgrade --install {releaseName} {QuoteArg(chart.ChartReference)}");
        arguments.Append(CultureInfo.InvariantCulture, $" --namespace {@namespace}");
        arguments.Append(" --create-namespace");
        arguments.Append(" --wait");

        arguments.Append(CultureInfo.InvariantCulture, $" --version {chart.ChartVersion}");

        if (chart.ForceConflicts)
        {
            // --force-conflicts tells helm's server-side apply to forcibly take over fields
            // owned by other field managers instead of failing with a conflict. Required
            // for charts whose admission webhooks are mutated by the AKS admissionsenforcer
            // / Azure Policy add-on after install — without it, helm's SSA fails on
            // .webhooks[*].namespaceSelector. Non-destructive (no resource recreate).
            // Equivalent to `kubectl apply --force-conflicts` and distinct from
            // --take-ownership (which transfers helm release ownership) and the
            // deprecated --force / --force-replace (which delete + recreate resources
            // and are incompatible with SSA).
            // See KubernetesHelmChartExtensions.WithForceConflicts for the full rationale.
            //
            // --server-side is REQUIRED alongside --force-conflicts: helm only registers
            // the --force-conflicts flag in server-side-apply mode. Both flags arrived
            // together in helm v3.18.
            //
            // We pass --server-side=true (with explicit value) rather than the bare
            // --server-side flag because helm v4 changed --server-side from a bool flag
            // to a string flag with "true"|"false"|"auto" values
            // (https://github.com/helm/helm/pull/13649). The bare form would consume the
            // following --force-conflicts as the flag value under v4, poisoning the
            // release metadata with the literal string "--force-conflicts" and breaking
            // every subsequent `helm upgrade` with
            // "invalid/unknown release server-side apply method: --force-conflicts".
            // The =true form parses identically under helm v3.18+ (where the bool flag
            // also accepts an explicit value) and helm v4.
            arguments.Append(" --server-side=true --force-conflicts");
        }

        if (environment.KubeConfigPath is not null)
        {
            arguments.Append(CultureInfo.InvariantCulture, $" --kubeconfig {QuoteArg(environment.KubeConfigPath)}");
        }

        foreach (var (key, value) in chart.Values)
        {
            arguments.Append(CultureInfo.InvariantCulture, $" --set {QuoteArg($"{key}={value}")}");
        }

        var stderrBuilder = new StringBuilder();

        var exitCode = await helmRunner.RunAsync(
            arguments.ToString(),
            onOutputData: output => logger.LogDebug("helm (stdout): {Output}", output),
            onErrorData: error =>
            {
                stderrBuilder.AppendLine(error);
                logger.LogDebug("helm (stderr): {Error}", error);
            },
            cancellationToken: context.CancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var errorOutput = stderrBuilder.ToString().Trim();
            var message = string.IsNullOrEmpty(errorOutput)
                ? $"helm upgrade --install for chart '{chart.Name}' failed with exit code {exitCode}"
                : $"helm upgrade --install for chart '{chart.Name}' failed: {errorOutput}";

            throw new InvalidOperationException(message);
        }

        if (chart.DestroyOnUninstall)
        {
            // Persist install state so destroy can find this release later, even from a
            // different process where the in-memory resource state is gone.
            var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
            var stateSection = await deploymentStateManager
                .AcquireSectionAsync(GetStateSectionName(environment, chart), context.CancellationToken)
                .ConfigureAwait(false);
            stateSection.Data["ReleaseName"] = releaseName;
            stateSection.Data["Namespace"] = @namespace;
            await deploymentStateManager.SaveSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Helm chart '{ChartName}' installed successfully as release '{ReleaseName}' in namespace '{Namespace}'.",
            chart.Name, releaseName, @namespace);
    }

    private static async Task UninstallHelmChartAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment,
        KubernetesHelmChartResource chart,
        string defaultReleaseName,
        string defaultNamespace)
    {
        var logger = context.Services.GetRequiredService<ILogger<KubernetesHelmChartResource>>();
        var helmRunner = context.Services.GetRequiredService<IHelmRunner>();
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();

        var stateSection = await deploymentStateManager
            .AcquireSectionAsync(GetStateSectionName(environment, chart), context.CancellationToken)
            .ConfigureAwait(false);
        var savedReleaseName = stateSection.Data["ReleaseName"]?.ToString();
        var savedNamespace = stateSection.Data["Namespace"]?.ToString();

        // Fall back to the values resolved at step-creation time when no state was persisted
        // (e.g., the user opted in to destroy after deploying without it). This is best-effort.
        var releaseName = !string.IsNullOrEmpty(savedReleaseName) ? savedReleaseName : defaultReleaseName;
        var @namespace = !string.IsNullOrEmpty(savedNamespace) ? savedNamespace : defaultNamespace;

        logger.LogInformation(
            "Uninstalling Helm release '{ReleaseName}' for chart '{ChartName}' from namespace '{Namespace}'.",
            releaseName, chart.Name, @namespace);

        var arguments = new StringBuilder();
        arguments.Append(CultureInfo.InvariantCulture, $"uninstall {releaseName} --namespace {@namespace}");

        if (environment.KubeConfigPath is not null)
        {
            arguments.Append(CultureInfo.InvariantCulture, $" --kubeconfig {QuoteArg(environment.KubeConfigPath)}");
        }

        var stderrBuilder = new StringBuilder();

        var exitCode = await helmRunner.RunAsync(
            arguments.ToString(),
            onOutputData: output => logger.LogDebug("helm (stdout): {Output}", output),
            onErrorData: error =>
            {
                stderrBuilder.AppendLine(error);
                logger.LogDebug("helm (stderr): {Error}", error);
            },
            cancellationToken: context.CancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            var errorOutput = stderrBuilder.ToString().Trim();
            var message = string.IsNullOrEmpty(errorOutput)
                ? $"helm uninstall for chart '{chart.Name}' failed with exit code {exitCode}"
                : $"helm uninstall for chart '{chart.Name}' failed: {errorOutput}";

            throw new InvalidOperationException(message);
        }

        await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Helm release '{ReleaseName}' uninstalled from namespace '{Namespace}'.",
            releaseName, @namespace);
    }

    private static (string ReleaseName, string Namespace) ResolveReleaseAndNamespace(KubernetesHelmChartResource chart)
    {
        var releaseName = chart.ReleaseName ?? chart.Name;
        var @namespace = chart.Namespace ?? chart.Name;

        // The fallback to chart.Name can produce a value that isn't a valid Helm release name or
        // Kubernetes namespace (uppercase, too long, etc.) — Aspire resource names allow more than
        // DNS labels do. Validate here so the caller gets a clear ArgumentException pointing at
        // WithReleaseName / WithNamespace instead of an opaque helm CLI failure.
        if (chart.ReleaseName is null)
        {
            try
            {
                HelmChartOptions.ValidateReleaseName(releaseName, nameof(chart.ReleaseName));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot derive a Helm release name from resource name '{chart.Name}'. " +
                    $"Set an explicit release name via WithReleaseName(...). {ex.Message}",
                    ex);
            }
        }

        if (chart.Namespace is null)
        {
            try
            {
                HelmChartOptions.ValidateNamespace(@namespace, nameof(chart.Namespace));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot derive a Kubernetes namespace from resource name '{chart.Name}'. " +
                    $"Set an explicit namespace via WithNamespace(...). {ex.Message}",
                    ex);
            }
        }

        return (releaseName, @namespace);
    }

    private static string GetStateSectionName(KubernetesEnvironmentResource environment, KubernetesHelmChartResource chart)
        => $"HelmChart:{environment.Name}:{chart.Name}";

    // Allowlist for Helm chart references. Covers OCI URLs (oci://host/path), HTTP/HTTPS URLs,
    // local paths, plain chart names ("repo/chart"), and packaged chart filenames. Rejects anything
    // that could break helm argument tokenization (whitespace, quotes, control chars).
    [GeneratedRegex(@"^[A-Za-z0-9_./:@+~\-]+$")]
    private static partial Regex ChartReferencePattern();

    // Disallowed in helm --set keys: anything that would break the key=value tokenization or
    // interact with helm's escape syntax. Allow alphanumerics plus dot, dash, underscore,
    // and brackets (for indexed/array paths like "args[0]").
    [GeneratedRegex(@"^[A-Za-z0-9_.\-\[\]]+$")]
    private static partial Regex HelmSetKeyPattern();

    private static void ValidateChartReference(string chartReference, string paramName)
    {
        if (!ChartReferencePattern().IsMatch(chartReference))
        {
            throw new ArgumentException(
                $"Helm chart reference '{chartReference}' is invalid. Use OCI/HTTP URLs, repo/chart names, or local paths containing only letters, digits, '.', '-', '_', '/', ':', '@', '+', '~'.",
                paramName);
        }
    }

    private static void ValidateHelmSetKey(string key, string paramName)
    {
        if (!HelmSetKeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                $"Helm value key '{key}' is invalid. Use letters, digits, '.', '-', '_', or brackets for indexed paths.",
                paramName);
        }
    }

    private static void ValidateHelmSetValue(string value, string paramName)
    {
        // Reject control characters (newlines, tabs) and double-quotes that would break the
        // surrounding quoted argument we hand to the OS process. Helm itself supports richer
        // value syntax via --set-file / --set-string, which users can wire up later if needed.
        foreach (var c in value)
        {
            if (c == '"' || c == '\\' || char.IsControl(c))
            {
                throw new ArgumentException(
                    $"Helm value contains an unsupported character (0x{(int)c:X2}). Avoid quotes, backslashes, and control characters; use --values files for complex values.",
                    paramName);
            }
        }
    }

    // Wraps an already-validated argument fragment in double quotes for safe interpolation
    // into the helm arguments string. Callers must validate that the value contains no
    // embedded quotes, backslashes, or control characters first.
    private static string QuoteArg(string value) => $"\"{value}\"";
}
