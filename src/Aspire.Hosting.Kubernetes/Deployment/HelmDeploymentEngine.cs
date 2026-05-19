// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREINTERACTION001

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Provides the Helm deployment engine that creates pipeline steps for deploying
/// Aspire applications to Kubernetes using Helm charts.
/// </summary>
internal static partial class HelmDeploymentEngine
{
    private const string HelmDeployTag = "helm-deploy";
    private const string HelmUninstallTag = "helm-uninstall";
    internal const string PrintSummaryTag = "print-summary";

    /// <summary>
    /// Gets the environment-specific values file name, mirroring Docker Compose's .env.{envName} pattern.
    /// </summary>
    internal static string GetDeployValuesFileName(string environmentName) => $"values.{environmentName}.yaml";

    /// <summary>
    /// Resolves the Helm release name. Uses the explicit annotation if set,
    /// otherwise falls back to the deployment environment name (<c>aspire deploy -e name</c>)
    /// lowercased for Helm compatibility.
    /// </summary>
    private static async Task<string> ResolveReleaseNameAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment)
    {
        if (environment.TryGetLastAnnotation<HelmReleaseNameAnnotation>(out var releaseAnnotation))
        {
            var resolvedRelease = await releaseAnnotation.ReleaseName.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedRelease))
            {
                ValidateHelmReleaseName(resolvedRelease);
                return resolvedRelease;
            }
        }

        // Default to the deployment environment name (aspire deploy -e <name>), not the resource name.
        var hostEnvironment = context.Services.GetRequiredService<IHostEnvironment>();
        var releaseName = hostEnvironment.EnvironmentName.ToLowerInvariant();
        ValidateHelmReleaseName(releaseName);
        return releaseName;
    }

    /// <summary>
    /// Resolves the target Kubernetes namespace from the annotation, defaulting to "default".
    /// </summary>
    private static async Task<string> ResolveNamespaceAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment)
    {
        if (environment.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation))
        {
            var resolvedNs = await nsAnnotation.Namespace.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(resolvedNs))
            {
                ValidateKubernetesNamespace(resolvedNs);
                return resolvedNs;
            }
        }

        return "default";
    }

    private const int HelmReleaseNameMaxLength = 53;
    private const int KubernetesNamespaceMaxLength = 63;

    private static void ValidateHelmReleaseName(string releaseName)
    {
        if (releaseName.Length > HelmReleaseNameMaxLength || !DnsLabelPattern().IsMatch(releaseName))
        {
            throw new InvalidOperationException(
                $"Helm release name '{releaseName}' is invalid. Use lowercase letters, numbers, and hyphens, " +
                $"start and end with an alphanumeric character, and stay within {HelmReleaseNameMaxLength} characters. " +
                "Set an explicit release name with .WithHelm(h => h.WithReleaseName(\"my-release\")).");
        }
    }

    private static void ValidateKubernetesNamespace(string @namespace)
    {
        if (@namespace.Length > KubernetesNamespaceMaxLength || !DnsLabelPattern().IsMatch(@namespace))
        {
            throw new InvalidOperationException(
                $"Kubernetes namespace '{@namespace}' is invalid. Use lowercase letters, numbers, and hyphens, " +
                $"start and end with an alphanumeric character, and stay within {KubernetesNamespaceMaxLength} characters. " +
                "Set an explicit namespace with .WithHelm(h => h.WithNamespace(\"my-namespace\")).");
        }
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex DnsLabelPattern();

    /// <summary>
    /// Creates the deployment pipeline steps for the Helm engine.
    /// </summary>
    internal static Task<IReadOnlyList<PipelineStep>> CreateStepsAsync(
        KubernetesEnvironmentResource environment,
        PipelineStepFactoryContext factoryContext)
    {
        var model = factoryContext.PipelineContext.Model;
        var steps = new List<PipelineStep>();

        // Step 0: Check prerequisites — verify Helm CLI is available
        var checkPrereqStep = new PipelineStep
        {
            Name = $"check-helm-prereqs-{environment.Name}",
            Description = $"Verifies Helm CLI is available for {environment.Name}.",
            Action = ctx =>
            {
                var helmPath = PathLookupHelper.FindFullPathFromPath("helm");
                if (helmPath is null)
                {
                    throw new InvalidOperationException(
                        "Helm CLI not found. Install it from https://helm.sh/docs/intro/install/ " +
                        "and ensure it is available on your PATH.");
                }

                ctx.Logger.LogDebug("Helm CLI found at: {HelmPath}", helmPath);
                return Task.CompletedTask;
            }
        };
        steps.Add(checkPrereqStep);

        // Step 1: Prepare - resolve values.yaml with actual image references and parameter values
        var prepareStep = new PipelineStep
        {
            Name = $"prepare-{environment.Name}",
            Description = $"Prepares Helm chart values for {environment.Name}.",
            Action = ctx => PrepareAsync(ctx, environment)
        };
        prepareStep.DependsOn(WellKnownPipelineSteps.Publish);
        prepareStep.DependsOn(WellKnownPipelineSteps.Build);
        prepareStep.DependsOn($"check-helm-prereqs-{environment.Name}");
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

        // Step 3: Print deployment instructions (dashboard access, Helm commands)
        var instructionsStep = new PipelineStep
        {
            Name = $"print-{environment.Name}-instructions",
            Description = $"Prints access instructions for {environment.Name}.",
            Tags = [PrintSummaryTag],
            Action = ctx => PrintDeploymentInstructionsAsync(ctx, environment)
        };
        instructionsStep.DependsOn($"helm-deploy-{environment.Name}");
        instructionsStep.RequiredBy(WellKnownPipelineSteps.Deploy);
        steps.Add(instructionsStep);

        // Step 4: Destroy confirmation + uninstall (used by aspire destroy)
        var helmDestroyStep = new PipelineStep
        {
            Name = $"destroy-helm-{environment.Name}",
            Description = $"Confirms and destroys the Helm deployment for {environment.Name}.",
            Action = async ctx =>
            {
                // Check deployment state to verify this environment was actually deployed
                var deploymentStateManager = ctx.Services.GetRequiredService<IDeploymentStateManager>();
                var stateSection = await deploymentStateManager.AcquireSectionAsync($"Helm:{environment.Name}", ctx.CancellationToken).ConfigureAwait(false);
                var savedReleaseName = stateSection.Data["ReleaseName"]?.ToString();
                var savedNamespace = stateSection.Data["Namespace"]?.ToString();

                if (string.IsNullOrEmpty(savedReleaseName))
                {
                    await ctx.ReportingStep.CompleteAsync(
                        $"No Helm deployment state found for '{environment.Name}'. Nothing to destroy.",
                        CompletionState.Completed,
                        ctx.CancellationToken).ConfigureAwait(false);
                    return;
                }

                // Use saved state for the confirmation message (more accurate than recomputing)
                var @namespace = savedNamespace ?? "default";
                await ConfirmDestroyAsync(ctx, $"Uninstall Helm release '{savedReleaseName}' from namespace '{@namespace}'? This action cannot be undone.").ConfigureAwait(false);
                await HelmUninstallAsync(ctx, environment, savedReleaseName, @namespace).ConfigureAwait(false);

                ctx.Summary.Add("🗑️ Helm Release", savedReleaseName);
                ctx.Summary.Add("☸️ Namespace", @namespace);

                // Clean up deployment state for this environment
                await deploymentStateManager.DeleteSectionAsync(stateSection, ctx.CancellationToken).ConfigureAwait(false);
            },
            DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq]
        };
        helmDestroyStep.RequiredBy(WellKnownPipelineSteps.Destroy);
        steps.Add(helmDestroyStep);

        // Step 5: Helm uninstall (teardown, callable directly via aspire do without confirmation)
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
            && environment.CapturedHelmImageReferences.Count == 0
            && environment.CapturedHelmValueProviders.Count == 0)
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

        // Phase 4: Resolve generic IValueProvider references.
        // During publish, values backed by IValueProvider (e.g., Bicep output references,
        // connection strings) are written as empty placeholders. At deploy time, we call
        // GetValueAsync() to resolve the actual values from external sources.
        // This is cloud-provider agnostic — any IValueProvider implementation works.
        foreach (var valueProviderRef in environment.CapturedHelmValueProviders)
        {
            var resolvedValue = await valueProviderRef.ValueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (resolvedValue is not null)
            {
                SetOverrideValue(overrideValues, valueProviderRef.Section, valueProviderRef.ResourceKey, valueProviderRef.ValueKey, resolvedValue);
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

        var @namespace = await ResolveNamespaceAsync(context, environment).ConfigureAwait(false);
        var releaseName = await ResolveReleaseNameAsync(context, environment).ConfigureAwait(false);

        var deployTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Deploying **{environment.Name}** to Kubernetes namespace **{@namespace}** as Helm release **{releaseName}**"),
            context.CancellationToken).ConfigureAwait(false);

        await using (deployTask.ConfigureAwait(false))
        {
            try
            {
                var helmRunner = context.Services.GetRequiredService<IHelmRunner>();

                // Verify helm is available
                try
                {
                    var versionExitCode = await helmRunner.RunAsync("version --short", cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    if (versionExitCode != 0)
                    {
                        throw new InvalidOperationException("'helm' is installed but returned an error. Ensure 'helm' is properly configured and your cluster is accessible.");
                    }
                }
                catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
                {
                    throw new InvalidOperationException("'helm' was not found. Please install 'helm' and ensure it is available on your PATH to deploy to Kubernetes.", ex);
                }

                var valuesFilePath = Path.Combine(outputPath, "values.yaml");
                var arguments = new StringBuilder();
                arguments.Append(CultureInfo.InvariantCulture, $"upgrade --install {releaseName} \"{outputPath}\"");
                arguments.Append(CultureInfo.InvariantCulture, $" --namespace {@namespace}");
                arguments.Append(" --create-namespace");
                arguments.Append(" --wait");

                if (environment.KubeConfigPath is not null)
                {
                    arguments.Append(CultureInfo.InvariantCulture, $" --kubeconfig \"{environment.KubeConfigPath}\"");
                }

                if (File.Exists(valuesFilePath))
                {
                    arguments.Append(CultureInfo.InvariantCulture, $" -f \"{valuesFilePath}\"");
                }

                var deployValuesFilePath = Path.Combine(outputPath, GetDeployValuesFileName(environment.Name));
                if (File.Exists(deployValuesFilePath))
                {
                    arguments.Append(CultureInfo.InvariantCulture, $" -f \"{deployValuesFilePath}\"");
                }

                context.Logger.LogDebug("Running helm {Arguments}", arguments);

                var stderrBuilder = new StringBuilder();

                var exitCode = await helmRunner.RunAsync(
                    arguments.ToString(),
                    workingDirectory: outputPath,
                    onOutputData: output => context.Logger.LogDebug("helm (stdout): {Output}", output),
                    onErrorData: error =>
                    {
                        stderrBuilder.AppendLine(error);
                        context.Logger.LogDebug("helm (stderr): {Error}", error);
                    },
                    cancellationToken: context.CancellationToken).ConfigureAwait(false);

                if (exitCode != 0)
                {
                    var errorOutput = stderrBuilder.ToString().Trim();
                    var message = string.IsNullOrEmpty(errorOutput)
                        ? $"helm upgrade --install failed with exit code {exitCode}"
                        : $"helm upgrade --install failed: {errorOutput}";

                    throw new InvalidOperationException(message);
                }
                else
                {
                    // Persist deployment state so destroy can find the release
                    var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
                    var stateSection = await deploymentStateManager.AcquireSectionAsync($"Helm:{environment.Name}", context.CancellationToken).ConfigureAwait(false);
                    stateSection.Data["ReleaseName"] = releaseName;
                    stateSection.Data["Namespace"] = @namespace;
                    await deploymentStateManager.SaveSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);

                    await deployTask.CompleteAsync(
                        new MarkdownString($"Helm release **{releaseName}** deployed to namespace **{@namespace}**"),
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
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

    internal static async Task PrintResourceSummaryAsync(
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

        var @namespace = await ResolveNamespaceAsync(context, environment).ConfigureAwait(false);

        try
        {
            var endpoints = await GetServiceEndpointsAsync(computeResource.Name.ToServiceName(), @namespace, environment.KubeConfigPath, context.Logger, context.CancellationToken).ConfigureAwait(false);

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

    private static async Task PrintDeploymentInstructionsAsync(
        PipelineStepContext context,
        KubernetesEnvironmentResource environment)
    {
        var @namespace = await ResolveNamespaceAsync(context, environment).ConfigureAwait(false);
        var releaseName = await ResolveReleaseNameAsync(context, environment).ConfigureAwait(false);

        // Dashboard port-forward instructions
        if (environment.DashboardEnabled && environment.Dashboard?.Resource is KubernetesAspireDashboardResource)
        {
            var dashboardServiceName = environment.Dashboard.Resource.Name.ToKubernetesResourceName() + "-service";
            context.Summary.Add(
                "📊 Dashboard",
                new MarkdownString($"`kubectl port-forward -n {@namespace} svc/{dashboardServiceName} 18888:18888` then open [http://localhost:18888](http://localhost:18888)"));

            var dashboardDeploymentName = environment.Dashboard.Resource.Name.ToKubernetesResourceName();
            context.Summary.Add(
                "🔑 Dashboard login",
                new MarkdownString($"`kubectl logs -n {@namespace} -l app.kubernetes.io/component={dashboardDeploymentName} --tail=50` to retrieve the login token"));
        }

        // Helm status and resource inspection
        context.Summary.Add(
            "📋 Release status",
            new MarkdownString($"`helm status {releaseName} -n {@namespace}`"));

        context.Summary.Add(
            "📦 View resources",
            new MarkdownString($"`kubectl get all -n {@namespace} -l app.kubernetes.io/instance={releaseName}`"));

        // Helm uninstall
        context.Summary.Add(
            "🗑️ Uninstall",
            new MarkdownString($"`helm uninstall {releaseName} -n {@namespace}`"));
    }

    private static async Task HelmUninstallAsync(PipelineStepContext context, KubernetesEnvironmentResource environment)
    {
        var @namespace = await ResolveNamespaceAsync(context, environment).ConfigureAwait(false);
        var releaseName = await ResolveReleaseNameAsync(context, environment).ConfigureAwait(false);
        await HelmUninstallAsync(context, environment, releaseName, @namespace).ConfigureAwait(false);
    }

    private static async Task HelmUninstallAsync(PipelineStepContext context, KubernetesEnvironmentResource environment, string releaseName, string @namespace)
    {
        var uninstallTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Uninstalling Helm release **{releaseName}** from namespace **{@namespace}**"),
            context.CancellationToken).ConfigureAwait(false);

        await using (uninstallTask.ConfigureAwait(false))
        {
            try
            {
                var helmRunner = context.Services.GetRequiredService<IHelmRunner>();
                var arguments = $"uninstall {releaseName} --namespace {@namespace}";

                if (environment.KubeConfigPath is not null)
                {
                    arguments += $" --kubeconfig \"{environment.KubeConfigPath}\"";
                }

                context.Logger.LogDebug("Running helm {Arguments}", arguments);

                var exitCode = await helmRunner.RunAsync(
                    arguments,
                    onOutputData: output => context.Logger.LogDebug("helm (stdout): {Output}", output),
                    onErrorData: error => context.Logger.LogDebug("helm (stderr): {Error}", error),
                    cancellationToken: context.CancellationToken).ConfigureAwait(false);

                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"helm uninstall failed with exit code {exitCode}");
                }
                else
                {
                    await uninstallTask.CompleteAsync(
                        new MarkdownString($"Helm release **{releaseName}** uninstalled from namespace **{@namespace}**"),
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
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

    private static async Task ConfirmDestroyAsync(PipelineStepContext context, string message)
    {
        var options = context.Services.GetRequiredService<IOptions<PipelineOptions>>();

        if (!options.Value.SkipConfirmation)
        {
            var interactionService = context.Services.GetRequiredService<IInteractionService>();

            if (!interactionService.IsAvailable)
            {
                throw new InvalidOperationException(
                    "Cannot perform destructive operation without confirmation. Use --yes to skip the confirmation prompt in non-interactive mode.");
            }

            var result = await interactionService.PromptNotificationAsync(
                "Destroy environment",
                message,
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Confirmation,
                    ShowSecondaryButton = true,
                    ShowDismiss = false,
                    PrimaryButtonText = "Destroy",
                    SecondaryButtonText = "Cancel"
                },
                context.CancellationToken).ConfigureAwait(false);

            if (result.Canceled || !result.Data)
            {
                context.Logger.LogInformation("User canceled the destroy operation.");
                throw new OperationCanceledException("Destroy operation canceled by user.");
            }
        }
    }

    private static async Task<List<string>> GetServiceEndpointsAsync(
        string serviceName,
        string @namespace,
        string? kubeConfigPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var endpoints = new List<string>();

        var arguments = $"get service {serviceName} --namespace {@namespace} -o json";

        if (kubeConfigPath is not null)
        {
            arguments += $" --kubeconfig \"{kubeConfigPath}\"";
        }
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
}
