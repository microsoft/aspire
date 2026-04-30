// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Provides extension methods for adding and configuring external Helm charts
/// in a Kubernetes environment.
/// </summary>
public static class KubernetesHelmChartExtensions
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

        var environment = builder.Resource;
        var resource = new KubernetesHelmChartResource(name, environment)
        {
            ChartReference = chartReference,
            ChartVersion = chartVersion
        };

        var chartBuilder = builder.ApplicationBuilder.AddResource(resource);

        // Register a pipeline step to install this Helm chart
        resource.Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var installStep = new PipelineStep
            {
                Name = $"helm-install-{name}",
                Description = $"Installs Helm chart '{name}' ({chartReference}:{chartVersion})",
                Action = ctx => InstallHelmChartAsync(ctx, environment, resource)
            };

            // Run after the main application Helm deploy
            installStep.DependsOn($"helm-deploy-{environment.Name}");
            installStep.RequiredBy(WellKnownPipelineSteps.Deploy);

            return Task.FromResult<IEnumerable<PipelineStep>>([installStep]);
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

        builder.Resource.ReleaseName = releaseName;
        return builder;
    }

    /// <summary>
    /// Installs the Helm chart via helm upgrade --install.
    /// </summary>
    private static async Task InstallHelmChartAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment,
        KubernetesHelmChartResource chart)
    {
        var logger = context.Services.GetRequiredService<ILogger<KubernetesHelmChartResource>>();
        var helmRunner = context.Services.GetRequiredService<IHelmRunner>();

        var releaseName = chart.ReleaseName ?? chart.Name;
        var @namespace = chart.Namespace ?? chart.Name;
        var chartRef = chart.ChartReference ?? throw new InvalidOperationException($"Helm chart '{chart.Name}' has no chart reference configured.");
        var chartVersion = chart.ChartVersion;

        logger.LogInformation(
            "Installing Helm chart '{ChartName}' ({ChartRef}:{ChartVersion}) into namespace '{Namespace}'.",
            chart.Name, chartRef, chartVersion, @namespace);

        var arguments = new StringBuilder();
        arguments.Append(CultureInfo.InvariantCulture, $"upgrade --install {releaseName} {chartRef}");
        arguments.Append(CultureInfo.InvariantCulture, $" --namespace {@namespace}");
        arguments.Append(" --create-namespace");
        arguments.Append(" --wait");

        if (!string.IsNullOrEmpty(chartVersion))
        {
            arguments.Append(CultureInfo.InvariantCulture, $" --version {chartVersion}");
        }

        if (environment.KubeConfigPath is not null)
        {
            arguments.Append(CultureInfo.InvariantCulture, $" --kubeconfig \"{environment.KubeConfigPath}\"");
        }

        foreach (var (key, value) in chart.Values)
        {
            arguments.Append(CultureInfo.InvariantCulture, $" --set {key}={value}");
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

        logger.LogInformation(
            "Helm chart '{ChartName}' installed successfully as release '{ReleaseName}' in namespace '{Namespace}'.",
            chart.Name, releaseName, @namespace);
    }
}
