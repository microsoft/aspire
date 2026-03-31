// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Provides the Helm deployment engine that creates pipeline steps for deploying
/// Aspire applications to Kubernetes using Helm charts.
/// </summary>
internal static partial class HelmDeploymentEngine
{
    private const string HelmDeployTag = "helm-deploy";
    private const string HelmUninstallTag = "helm-uninstall";
    private const string PrintSummaryTag = "print-summary";

    /// <summary>
    /// Gets the environment-specific values file name, mirroring Docker Compose's .env.{envName} pattern.
    /// </summary>
    internal static string GetDeployValuesFileName(string environmentName) => $"values.{environmentName}.yaml";

    /// <summary>
    /// Creates the deployment pipeline steps for the Helm engine.
    /// </summary>
    internal static Task<IReadOnlyList<PipelineStep>> CreateStepsAsync(
        KubernetesEnvironmentResource environment,
        PipelineStepFactoryContext factoryContext)
    {
        var model = factoryContext.PipelineContext.Model;
        var steps = new List<PipelineStep>();

        // Step 1: Prepare - resolve values.yaml with actual image references and parameter values
        var prepareStep = new PipelineStep
        {
            Name = $"prepare-{environment.Name}",
            Description = $"Prepares Helm chart values for {environment.Name}.",
            Action = ctx => PrepareAsync(ctx, environment)
        };
        prepareStep.DependsOn(WellKnownPipelineSteps.Publish);
        prepareStep.DependsOn(WellKnownPipelineSteps.Build);
        steps.Add(prepareStep);

        // Step 2: Helm deploy - run helm upgrade --install
        var helmDeployStep = new PipelineStep
        {
            Name = $"helm-deploy-{environment.Name}",
            Description = $"Deploys {environment.Name} to Kubernetes via Helm.",
            Tags = [HelmDeployTag],
            Action = ctx => HelmDeployAsync(ctx, environment)
        };
        helmDeployStep.DependsOn($"prepare-{environment.Name}");
        helmDeployStep.RequiredBy(WellKnownPipelineSteps.Deploy);
        steps.Add(helmDeployStep);

        // Step 3: Print summary for each resource with external endpoints
        foreach (var computeResource in model.GetComputeResources())
        {
            var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(environment)?.DeploymentTarget;
            if (deploymentTarget is not KubernetesResource k8sResource)
            {
                continue;
            }

            var summaryStep = new PipelineStep
            {
                Name = $"print-{computeResource.Name}-summary",
                Description = $"Retrieves deployment status for {computeResource.Name}.",
                Tags = [PrintSummaryTag],
                Action = ctx => PrintResourceSummaryAsync(ctx, environment, computeResource, k8sResource)
            };
            summaryStep.DependsOn($"helm-deploy-{environment.Name}");
            summaryStep.RequiredBy(WellKnownPipelineSteps.Deploy);
            steps.Add(summaryStep);
        }

        // Step 4: Helm uninstall (teardown)
        var helmUninstallStep = new PipelineStep
        {
            Name = $"helm-uninstall-{environment.Name}",
            Description = $"Uninstalls the Helm release for {environment.Name}.",
            Tags = [HelmUninstallTag],
            Action = ctx => HelmUninstallAsync(ctx, environment)
        };
        steps.Add(helmUninstallStep);

        return Task.FromResult<IReadOnlyList<PipelineStep>>(steps);
    }

    private static async Task PrepareAsync(PipelineStepContext context, KubernetesEnvironmentResource environment)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, environment);
        var valuesFilePath = Path.Combine(outputPath, "values.yaml");

        if (!File.Exists(valuesFilePath))
        {
            context.Logger.LogDebug("No values.yaml found at {Path}, skipping prepare step.", valuesFilePath);
            return;
        }

        var prepareTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Preparing Helm chart values for **{environment.Name}**"),
            context.CancellationToken).ConfigureAwait(false);

        await using (prepareTask.ConfigureAwait(false))
        {
            try
            {
                // Update the chart version if configured via annotation
                if (environment.TryGetLastAnnotation<HelmChartVersionAnnotation>(out var versionAnnotation))
                {
                    var version = await versionAnnotation.Version.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(version))
                    {
                        environment.HelmChartVersion = version;

                        // Re-write Chart.yaml with updated version
                        var chartFilePath = Path.Combine(outputPath, "Chart.yaml");
                        if (File.Exists(chartFilePath))
                        {
                            var chartContent = await File.ReadAllTextAsync(chartFilePath, context.CancellationToken).ConfigureAwait(false);
                            // Simple replacement of the version line in Chart.yaml
                            chartContent = System.Text.RegularExpressions.Regex.Replace(
                                chartContent,
                                @"^version:\s*.*$",
                                $"version: {version}",
                                System.Text.RegularExpressions.RegexOptions.Multiline);
                            await File.WriteAllTextAsync(chartFilePath, chartContent, context.CancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                // Resolve captured parameter/secret values and write a deploy override file.
                // During publish, secrets and parameters without defaults are written as empty
                // placeholders in values.yaml. During deploy, we resolve them and provide the
                // actual values via a separate override file passed to helm.
                await ResolveAndWriteDeployValuesAsync(outputPath, environment, context.CancellationToken).ConfigureAwait(false);

                await prepareTask.CompleteAsync(
                    new MarkdownString($"Helm chart values prepared for **{environment.Name}**"),
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await prepareTask.CompleteAsync(
                    $"Failed to prepare Helm chart values: {ex.Message}",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// Resolves captured parameter/secret values, cross-resource references, and container image references
    /// from the publish step, then writes an environment-specific values override file for use during helm upgrade --install.
    /// </summary>
    internal static async Task ResolveAndWriteDeployValuesAsync(
        string outputPath,
        KubernetesEnvironmentResource environment,
        CancellationToken cancellationToken)
    {
        if (environment.CapturedHelmValues.Count == 0
            && environment.CapturedHelmCrossReferences.Count == 0
            && environment.CapturedHelmImageReferences.Count == 0)
        {
            return;
        }

        // Build the override structure: { section: { resourceKey: { valueKey: resolvedValue } } }
        var overrideValues = new Dictionary<string, Dictionary<string, Dictionary<string, object>>>();

        // Phase 1: Resolve direct ParameterResource values
        // Also build a flat lookup for cross-reference substitution: "section.resourceKey.valueKey" → resolvedValue
        var resolvedLookup = new Dictionary<string, string>();

        foreach (var captured in environment.CapturedHelmValues)
        {
            var resolvedValue = await captured.Parameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (resolvedValue is null)
            {
                continue;
            }

            SetOverrideValue(overrideValues, captured.Section, captured.ResourceKey, captured.ValueKey, resolvedValue);
            resolvedLookup[$"{captured.Section}.{captured.ResourceKey}.{captured.ValueKey}"] = resolvedValue;
        }

        // Phase 2: Resolve cross-resource secret references by substituting Helm expressions
        // in the template value with values resolved in Phase 1.
        foreach (var crossRef in environment.CapturedHelmCrossReferences)
        {
            var resolvedValue = ResolveHelmExpressions(crossRef.TemplateValue, resolvedLookup);
            SetOverrideValue(overrideValues, crossRef.Section, crossRef.ResourceKey, crossRef.ValueKey, resolvedValue);
        }

        // Phase 3: Resolve container image references with registry-prefixed names.
        // During publish, images are written as "server:latest". At deploy time, we resolve
        // the full image name including the container registry (e.g., "myregistry.azurecr.io/server:latest")
        // using the same ContainerImageReference pattern as Docker Compose.
        foreach (var imageRef in environment.CapturedHelmImageReferences)
        {
            IValueProvider cir = new ContainerImageReference(imageRef.Resource);
            var resolvedImage = await cir.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (resolvedImage is not null)
            {
                SetOverrideValue(overrideValues, imageRef.Section, imageRef.ResourceKey, imageRef.ValueKey, resolvedImage);
            }
        }

        if (overrideValues.Count > 0)
        {
            var serializer = new YamlDotNet.Serialization.SerializerBuilder()
                .WithNewLine("\n")
                .Build();
            var overrideContent = serializer.Serialize(overrideValues);
            var overrideFilePath = Path.Combine(outputPath, GetDeployValuesFileName(environment.Name));
            await File.WriteAllTextAsync(overrideFilePath, overrideContent, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void SetOverrideValue(
        Dictionary<string, Dictionary<string, Dictionary<string, object>>> overrideValues,
        string section, string resourceKey, string valueKey, object value)
    {
        if (!overrideValues.TryGetValue(section, out var sectionDict))
        {
            sectionDict = [];
            overrideValues[section] = sectionDict;
        }

        if (!sectionDict.TryGetValue(resourceKey, out var resourceValues))
        {
            resourceValues = [];
            sectionDict[resourceKey] = resourceValues;
        }

        resourceValues[valueKey] = value;
    }

    /// <summary>
    /// Substitutes Helm value expressions (e.g., <c>{{ .Values.secrets.cache.password }}</c>) in a template
    /// string with resolved values from the lookup dictionary.
    /// </summary>
    internal static string ResolveHelmExpressions(string template, Dictionary<string, string> resolvedLookup)
    {
        // Match Helm expressions like {{ .Values.secrets.cache.password }} or {{ .Values.config.myapp.key }}
        return HelmValuesExpressionRegex().Replace(template, match =>
        {
            var path = match.Groups[1].Value.Trim();

            // Path is like ".Values.secrets.cache.password" → normalize to "secrets.cache.password"
            if (path.StartsWith(".Values.", StringComparison.Ordinal))
            {
                path = path[".Values.".Length..];
            }

            // Convert to the same key format used in resolvedLookup (underscore-based)
            path = path.Replace("-", "_");

            return resolvedLookup.TryGetValue(path, out var resolved) ? resolved : match.Value;
        });
    }

    [GeneratedRegex(@"\{\{\s*(\.Values\.[a-zA-Z0-9_.]+)\s*\}\}")]
    private static partial Regex HelmValuesExpressionRegex();

    private static async Task HelmDeployAsync(PipelineStepContext context, KubernetesEnvironmentResource environment)
    {
        var outputPath = PublishingContextUtils.GetEnvironmentOutputPath(context, environment);

        // Resolve namespace from annotation or use default
        var @namespace = "default";
        if (environment.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation))
        {
            var resolvedNs = await nsAnnotation.Namespace.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedNs))
            {
                @namespace = resolvedNs;
            }
        }

        // Resolve release name from annotation or derive from environment name
        var releaseName = environment.Name;
        if (environment.TryGetLastAnnotation<HelmReleaseNameAnnotation>(out var releaseAnnotation))
        {
            var resolvedRelease = await releaseAnnotation.ReleaseName.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedRelease))
            {
                releaseName = resolvedRelease;
            }
        }

        var deployTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Deploying **{environment.Name}** to Kubernetes namespace **{@namespace}** as Helm release **{releaseName}**"),
            context.CancellationToken).ConfigureAwait(false);

        await using (deployTask.ConfigureAwait(false))
        {
            try
            {
                // Verify helm is available
                await VerifyToolAvailableAsync("helm", context.CancellationToken).ConfigureAwait(false);

                var valuesFilePath = Path.Combine(outputPath, "values.yaml");
                var arguments = new StringBuilder();
                arguments.Append(CultureInfo.InvariantCulture, $"upgrade --install {releaseName} \"{outputPath}\"");
                arguments.Append(CultureInfo.InvariantCulture, $" --namespace {@namespace}");
                arguments.Append(" --create-namespace");
                arguments.Append(" --wait");

                if (File.Exists(valuesFilePath))
                {
                    arguments.Append(CultureInfo.InvariantCulture, $" -f \"{valuesFilePath}\"");
                }

                // Pass deploy-time override values (resolved secrets/parameters) after the
                // base values.yaml so they take precedence via Helm's merge behavior.
                var deployValuesFilePath = Path.Combine(outputPath, GetDeployValuesFileName(environment.Name));
                if (File.Exists(deployValuesFilePath))
                {
                    arguments.Append(CultureInfo.InvariantCulture, $" -f \"{deployValuesFilePath}\"");
                }

                context.Logger.LogDebug("Running helm {Arguments}", arguments);

                var stdoutBuilder = new StringBuilder();
                var stderrBuilder = new StringBuilder();

                var spec = new ProcessSpec("helm")
                {
                    Arguments = arguments.ToString(),
                    WorkingDirectory = outputPath,
                    ThrowOnNonZeroReturnCode = false,
                    InheritEnv = true,
                    OnOutputData = output =>
                    {
                        stdoutBuilder.AppendLine(output);
                        context.Logger.LogDebug("helm (stdout): {Output}", output);
                    },
                    OnErrorData = error =>
                    {
                        stderrBuilder.AppendLine(error);
                        context.Logger.LogDebug("helm (stderr): {Error}", error);
                    },
                };

                var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

                await using (processDisposable.ConfigureAwait(false))
                {
                    var processResult = await pendingProcessResult
                        .WaitAsync(context.CancellationToken)
                        .ConfigureAwait(false);

                    if (processResult.ExitCode != 0)
                    {
                        var errorOutput = stderrBuilder.ToString().Trim();
                        var message = string.IsNullOrEmpty(errorOutput)
                            ? $"helm upgrade --install failed with exit code {processResult.ExitCode}"
                            : $"helm upgrade --install failed: {errorOutput}";

                        await deployTask.FailAsync(message, cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await deployTask.CompleteAsync(
                            new MarkdownString($"Helm release **{releaseName}** deployed to namespace **{@namespace}**"),
                            CompletionState.Completed,
                            context.CancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await deployTask.CompleteAsync(
                    $"Helm deployment failed: {ex.Message}",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task PrintResourceSummaryAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment,
        IResource computeResource,
        KubernetesResource k8sResource)
    {
        // Only print summaries for resources with external-facing services
        if (k8sResource.Service is null)
        {
            return;
        }

        // Resolve namespace
        var @namespace = "default";
        if (environment.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation))
        {
            var resolvedNs = await nsAnnotation.Namespace.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedNs))
            {
                @namespace = resolvedNs;
            }
        }

        try
        {
            var endpoints = await GetServiceEndpointsAsync(computeResource.Name, @namespace, context.Logger, context.CancellationToken).ConfigureAwait(false);

            if (endpoints.Count > 0)
            {
                var endpointText = string.Join(", ", endpoints.Select(e => $"[{e}]({e})"));
                context.Summary.Add(computeResource.Name, endpointText);
                context.Logger.LogInformation("Resource {ResourceName}: {Endpoints}", computeResource.Name, endpointText);
            }
            else
            {
                context.Logger.LogDebug("No external endpoints found for {ResourceName}", computeResource.Name);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "Failed to retrieve endpoints for {ResourceName}", computeResource.Name);
        }
    }

    private static async Task HelmUninstallAsync(PipelineStepContext context, KubernetesEnvironmentResource environment)
    {
        var @namespace = "default";
        if (environment.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation))
        {
            var resolvedNs = await nsAnnotation.Namespace.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedNs))
            {
                @namespace = resolvedNs;
            }
        }

        var releaseName = environment.Name;
        if (environment.TryGetLastAnnotation<HelmReleaseNameAnnotation>(out var releaseAnnotation))
        {
            var resolvedRelease = await releaseAnnotation.ReleaseName.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedRelease))
            {
                releaseName = resolvedRelease;
            }
        }

        var uninstallTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Uninstalling Helm release **{releaseName}** from namespace **{@namespace}**"),
            context.CancellationToken).ConfigureAwait(false);

        await using (uninstallTask.ConfigureAwait(false))
        {
            try
            {
                var arguments = $"uninstall {releaseName} --namespace {@namespace}";

                context.Logger.LogDebug("Running helm {Arguments}", arguments);

                var spec = new ProcessSpec("helm")
                {
                    Arguments = arguments,
                    ThrowOnNonZeroReturnCode = false,
                    InheritEnv = true,
                    OnOutputData = output => context.Logger.LogDebug("helm (stdout): {Output}", output),
                    OnErrorData = error => context.Logger.LogDebug("helm (stderr): {Error}", error),
                };

                var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

                await using (processDisposable.ConfigureAwait(false))
                {
                    var processResult = await pendingProcessResult
                        .WaitAsync(context.CancellationToken)
                        .ConfigureAwait(false);

                    if (processResult.ExitCode != 0)
                    {
                        await uninstallTask.FailAsync(
                            $"helm uninstall failed with exit code {processResult.ExitCode}",
                            cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await uninstallTask.CompleteAsync(
                            new MarkdownString($"Helm release **{releaseName}** uninstalled from namespace **{@namespace}**"),
                            CompletionState.Completed,
                            context.CancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await uninstallTask.CompleteAsync(
                    $"Helm uninstall failed: {ex.Message}",
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<List<string>> GetServiceEndpointsAsync(
        string serviceName,
        string @namespace,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var endpoints = new List<string>();

        var arguments = $"get service {serviceName} --namespace {@namespace} -o json";
        var stdoutBuilder = new StringBuilder();

        var spec = new ProcessSpec("kubectl")
        {
            Arguments = arguments,
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = output => stdoutBuilder.AppendLine(output),
            OnErrorData = error => logger.LogDebug("kubectl (stderr): {Error}", error),
        };

        var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

        await using (processDisposable.ConfigureAwait(false))
        {
            var processResult = await pendingProcessResult
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (processResult.ExitCode != 0)
            {
                return endpoints;
            }
        }

        try
        {
            var json = stdoutBuilder.ToString();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var serviceType = root.GetProperty("spec").GetProperty("type").GetString();

            if (serviceType is "LoadBalancer")
            {
                if (root.TryGetProperty("status", out var status) &&
                    status.TryGetProperty("loadBalancer", out var lb) &&
                    lb.TryGetProperty("ingress", out var ingress))
                {
                    foreach (var entry in ingress.EnumerateArray())
                    {
                        var host = entry.TryGetProperty("ip", out var ip) ? ip.GetString()
                            : entry.TryGetProperty("hostname", out var hostname) ? hostname.GetString()
                            : null;

                        if (host is not null)
                        {
                            foreach (var port in root.GetProperty("spec").GetProperty("ports").EnumerateArray())
                            {
                                var portNumber = port.GetProperty("port").GetInt32();
                                var scheme = portNumber == 443 ? "https" : "http";
                                endpoints.Add($"{scheme}://{host}:{portNumber}");
                            }
                        }
                    }
                }
            }
            else if (serviceType is "NodePort")
            {
                foreach (var port in root.GetProperty("spec").GetProperty("ports").EnumerateArray())
                {
                    if (port.TryGetProperty("nodePort", out var nodePort))
                    {
                        var portNumber = nodePort.GetInt32();
                        endpoints.Add($"http://localhost:{portNumber}");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse kubectl output for service {ServiceName}", serviceName);
        }

        return endpoints;
    }

    private static async Task VerifyToolAvailableAsync(string tool, CancellationToken cancellationToken)
    {
        var spec = new ProcessSpec(tool)
        {
            Arguments = "version --short",
            ThrowOnNonZeroReturnCode = false,
            InheritEnv = true,
            OnOutputData = _ => { },
            OnErrorData = _ => { },
        };

        try
        {
            var (pendingProcessResult, processDisposable) = ProcessUtil.Run(spec);

            await using (processDisposable.ConfigureAwait(false))
            {
                var result = await pendingProcessResult
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"'{tool}' is installed but returned an error. Ensure '{tool}' is properly configured and your cluster is accessible.");
                }
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"'{tool}' was not found. Please install '{tool}' and ensure it is available on your PATH to deploy to Kubernetes.", ex);
        }
    }
}
