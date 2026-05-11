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
    [AspireExport(Description = "Adds an external Helm chart to a Kubernetes environment")]
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
    [AspireExport(Description = "Sets a Helm value for chart installation")]
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
    [AspireExport("withHelmChartNamespace", Description = "Sets the namespace for Helm chart installation")]
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
    [AspireExport("withHelmChartReleaseName", Description = "Sets the release name for Helm chart installation")]
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
    [AspireExport("withHelmChartDestroy", Description = "Uninstalls the Helm chart on aspire destroy")]
    public static IResourceBuilder<KubernetesHelmChartResource> WithDestroy(
        this IResourceBuilder<KubernetesHelmChartResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.DestroyOnUninstall = true;
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
