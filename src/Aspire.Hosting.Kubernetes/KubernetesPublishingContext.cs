// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Kubernetes.Yaml;
using Aspire.Hosting.Yaml;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.Kubernetes;

internal sealed class KubernetesPublishingContext(
    DistributedApplicationExecutionContext executionContext,
    string outputPath,
    ILogger logger,
    KubernetesEnvironmentResource? environment = null,
    CancellationToken cancellationToken = default)
{
    public readonly string OutputPath = outputPath;

    private readonly Dictionary<string, Dictionary<string, object>> _helmValues = new()
    {
        [HelmExtensions.ParametersKey] = new Dictionary<string, object>(),
        [HelmExtensions.SecretsKey] = new Dictionary<string, object>(),
        [HelmExtensions.ConfigKey] = new Dictionary<string, object>(),
    };

    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ByteArrayStringYamlConverter())
        .WithTypeConverter(new IntOrStringYamlConverter())
        .WithEventEmitter(nextEmitter => new ForceQuotedStringsEventEmitter(nextEmitter, HelmExtensions.ShouldDoubleQuoteString))
        .WithEventEmitter(e => new FloatEmitter(e))
        .WithEmissionPhaseObjectGraphVisitor(args => new YamlIEnumerableSkipEmptyObjectGraphVisitor(args.InnerVisitor))
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithNewLine("\n")
        .WithIndentedSequences()
        .Build();

    internal async Task WriteModelAsync(DistributedApplicationModel model, KubernetesEnvironmentResource environment)
    {
        if (!executionContext.IsPublishMode)
        {
            return;
        }

        logger.StartGeneratingKubernetes();

        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(OutputPath);

        if (model.Resources.Count == 0)
        {
            logger.EmptyModel();
            return;
        }

        await WriteKubernetesOutputAsync(model, environment).ConfigureAwait(false);

        logger.FinishGeneratingKubernetes(OutputPath);
    }

    private async Task WriteKubernetesOutputAsync(DistributedApplicationModel model, KubernetesEnvironmentResource environment)
    {
        // Include the dashboard resource alongside model resources so its templates are generated.
        // This mirrors the Docker Compose pattern in DockerComposePublishingContext.
        IEnumerable<IResource> resources = environment.DashboardEnabled && environment.Dashboard?.Resource is IResource dashboardResource
            ? [dashboardResource, .. model.Resources]
            : model.Resources;

        foreach (var resource in resources)
        {
            // Check for deployment target matching this environment or its parent (e.g., AKS)
            var targetEnv = (IComputeEnvironmentResource?)environment.OwningComputeEnvironment ?? environment;
            if (resource.GetDeploymentTargetAnnotation(targetEnv)?.DeploymentTarget is KubernetesResource serviceResource)
            {
                // Materialize Dockerfile factory if present
                if (serviceResource.TargetResource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuildAnnotation) &&
                    dockerfileBuildAnnotation.DockerfileFactory is not null)
                {
                    var dockerfileContext = new DockerfileFactoryContext
                    {
                        Services = executionContext.ServiceProvider,
                        Resource = serviceResource.TargetResource,
                        CancellationToken = cancellationToken
                    };
                    await dockerfileBuildAnnotation.MaterializeDockerfileAsync(dockerfileContext, cancellationToken).ConfigureAwait(false);

                    // Copy to a resource-specific path in the output folder for publishing
                    var resourceDockerfilePath = Path.Combine(OutputPath, $"{serviceResource.TargetResource.Name}.Dockerfile");
                    Directory.CreateDirectory(OutputPath);
                    File.Copy(dockerfileBuildAnnotation.DockerfilePath, resourceDockerfilePath, overwrite: true);
                }

                if (serviceResource.TargetResource.TryGetAnnotationsOfType<KubernetesServiceCustomizationAnnotation>(out var annotations))
                {
                    foreach (var a in annotations)
                    {
                        a.Configure(serviceResource);
                    }
                }

                // Apply node pool nodeSelector if the resource has a node pool annotation
                if (serviceResource.TargetResource.TryGetLastAnnotation<KubernetesNodePoolAnnotation>(out var nodePoolAnnotation))
                {
                    ApplyNodePoolSelector(serviceResource, nodePoolAnnotation.NodePool);
                }

                await WriteKubernetesTemplatesForResource(resource, serviceResource.GetTemplatedResources()).ConfigureAwait(false);
                await AppendResourceContextToHelmValuesAsync(resource, serviceResource).ConfigureAwait(false);
            }
        }

        // Write Ingress resources as standalone templates.
        foreach (var ingressResource in resources.OfType<KubernetesIngressResource>())
        {
            if (ingressResource.Parent == environment && ingressResource.GeneratedIngress is { } generatedIngress)
            {
                await WriteKubernetesTemplatesForResource(ingressResource, [generatedIngress]).ConfigureAwait(false);
            }
        }

        // Write Gateway API resources (Gateway + HTTPRoutes) as standalone templates.
        foreach (var gatewayResource in resources.OfType<KubernetesGatewayResource>())
        {
            if (gatewayResource.Parent == environment && gatewayResource.GeneratedGateway is { } generatedGateway)
            {
                var gatewayObjects = new List<BaseKubernetesResource> { generatedGateway };
                gatewayObjects.AddRange(gatewayResource.GeneratedHttpRoutes);
                await WriteKubernetesTemplatesForResource(gatewayResource, gatewayObjects).ConfigureAwait(false);
            }
        }

        await WriteKubernetesHelmChartAsync(environment).ConfigureAwait(false);
        await WriteKubernetesHelmValuesAsync().ConfigureAwait(false);
    }

    private async Task AppendResourceContextToHelmValuesAsync(IResource resource, KubernetesResource resourceContext)
    {
        await AddValuesToHelmSectionAsync(resource, resourceContext.Parameters, HelmExtensions.ParametersKey).ConfigureAwait(false);

        // Merge AdditionalConfigValues (e.g., branch parameters from if/else conditionals)
        // into a combined dictionary for the config section of values.yaml.
        var configItems = new Dictionary<string, KubernetesResource.HelmValue>(resourceContext.EnvironmentVariables);
        foreach (var kvp in resourceContext.AdditionalConfigValues)
        {
            configItems.TryAdd(kvp.Key, kvp.Value);
        }

        await AddValuesToHelmSectionAsync(resource, configItems, HelmExtensions.ConfigKey).ConfigureAwait(false);
        await AddValuesToHelmSectionAsync(resource, resourceContext.Secrets, HelmExtensions.SecretsKey).ConfigureAwait(false);
    }

    private async Task AddValuesToHelmSectionAsync(
        IResource resource,
        Dictionary<string, KubernetesResource.HelmValue> contextItems,
        string helmKey)
    {
        if (contextItems.Count <= 0 || _helmValues[helmKey] is not Dictionary<string, object> helmSection)
        {
            return;
        }

        var paramValues = new Dictionary<string, object>();

        foreach (var (key, helmExpressionWithValue) in contextItems)
        {
            // Use ValuesKey when available to ensure values.yaml key matches the Helm expression path.
            // This matters when the dictionary key (env var name, e.g., "REDIS_PASSWORD") differs from
            // the parameter name used in the Helm expression (e.g., "cache_password").
            var valuesKey = helmExpressionWithValue.ValuesKey ?? key.ToHelmValuesSectionName();

            // Cross-resource secret references have their Value set to a string containing
            // Helm expressions (e.g., "cache:6379,password={{ .Values.secrets.cache.password }}").
            // These need empty placeholders in values.yaml so the YAML section structure exists,
            // and the actual values are resolved at deploy time from the captured cross-reference.
            if (helmExpressionWithValue.ValueContainsHelmExpression)
            {
                paramValues[valuesKey] = string.Empty;
                environment?.CapturedHelmCrossReferences.Add(
                    new KubernetesEnvironmentResource.CapturedHelmCrossReference(
                        helmKey,
                        resource.Name.ToHelmValuesSectionName(),
                        valuesKey,
                        helmExpressionWithValue.ValueString!));
                continue;
            }

            object? value;

            // If there's a parameter source, resolve its value asynchronously
            if (helmExpressionWithValue.ParameterSource is ParameterResource parameter)
            {
                if (parameter.Secret || parameter.Default is null)
                {
                    // Don't resolve secrets or parameters without defaults during publish.
                    // Write an empty placeholder and capture the mapping for deploy-time resolution.
                    value = string.Empty;
                    environment?.CapturedHelmValues.Add(
                        new KubernetesEnvironmentResource.CapturedHelmValue(
                            helmKey,
                            resource.Name.ToHelmValuesSectionName(),
                            valuesKey,
                            parameter));
                }
                else
                {
                    value = await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                value = helmExpressionWithValue.Value;

                // If the value has an IValueProvider source, capture it for deploy-time
                // resolution. Write an empty placeholder now and resolve at deploy time.
                // This handles Bicep output references, connection strings, and any other
                // deferred value source without requiring Azure-specific knowledge.
                if (helmExpressionWithValue.ValueProviderSource is { } valueProvider)
                {
                    value = string.Empty;
                    environment?.CapturedHelmValueProviders.Add(
                        new KubernetesEnvironmentResource.CapturedHelmValueProvider(
                            helmKey,
                            resource.Name.ToHelmValuesSectionName(),
                            valuesKey,
                            valueProvider));
                }
            }

            paramValues[valuesKey] = value ?? string.Empty;

            // Capture container image references for deploy-time registry resolution.
            // During publish, the default image name (e.g., "server:latest") is written to values.yaml.
            // During deploy, the ContainerImageReference resolves the full registry-prefixed name
            // (e.g., "myregistry.azurecr.io/server:latest") and writes it to the override file.
            if (helmExpressionWithValue.ImageResource is not null)
            {
                environment?.CapturedHelmImageReferences.Add(
                    new KubernetesEnvironmentResource.CapturedHelmImageReference(
                        helmKey,
                        resource.Name.ToHelmValuesSectionName(),
                        valuesKey,
                        helmExpressionWithValue.ImageResource));
            }
        }

        if (paramValues.Count > 0)
        {
            helmSection[resource.Name.ToHelmValuesSectionName()] = paramValues;
        }
    }

    private async Task WriteKubernetesTemplatesForResource(IResource resource, IEnumerable<BaseKubernetesResource> templatedItems)
    {
        var templatesFolder = Path.Combine(OutputPath, "templates", resource.Name);
        Directory.CreateDirectory(templatesFolder);

        foreach (var templatedItem in templatedItems)
        {
            var fileName = GetFilename(resource.Name, templatedItem);
            var outputFile = Path.Combine(templatesFolder, fileName);
            var yaml = _serializer.Serialize(templatedItem);

            using var writer = new StreamWriter(outputFile);
            await writer.WriteLineAsync(HelmExtensions.TemplateFileSeparator).ConfigureAwait(false);
            await writer.WriteAsync(yaml).ConfigureAwait(false);
        }
    }

    private static string GetFilename(string baseName, BaseKubernetesResource templatedItem)
    {
        if (string.IsNullOrWhiteSpace(templatedItem.Metadata.Name))
        {
            return $"{templatedItem.GetType().Name.ToLowerInvariant()}.yaml";
        }

        var resourceName = templatedItem.Metadata.Name;
        if (resourceName.StartsWith($"{baseName.ToLowerInvariant()}-"))
        {
            resourceName = resourceName.Substring(baseName.Length + 1); // +1 for the hyphen
        }

        return $"{resourceName}.yaml";
    }

    private async Task WriteKubernetesHelmValuesAsync()
    {
        var valuesYaml = _serializer.Serialize(_helmValues);
        var outputFile = Path.Combine(OutputPath!, "values.yaml");
        Directory.CreateDirectory(OutputPath!);
        await File.WriteAllTextAsync(outputFile, valuesYaml, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteKubernetesHelmChartAsync(KubernetesEnvironmentResource environment)
    {
        var helmChart = new HelmChart
        {
            Name = environment.HelmChartName,
            Version = environment.HelmChartVersion,
            AppVersion = environment.HelmChartVersion,
            Description = environment.HelmChartDescription,
            Type = "application",
            ApiVersion = "v2",
            Keywords = ["aspire", "kubernetes"],
            KubeVersion = ">= 1.18.0-0",
        };

        var chartYaml = _serializer.Serialize(helmChart);
        var outputFile = Path.Combine(OutputPath, "Chart.yaml");
        Directory.CreateDirectory(OutputPath);
        await File.WriteAllTextAsync(outputFile, chartYaml, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyNodePoolSelector(KubernetesResource serviceResource, KubernetesNodePoolResource nodePool)
    {
        var podSpec = serviceResource.Workload?.PodTemplate?.Spec;
        if (podSpec is null)
        {
            return;
        }

        podSpec.NodeSelector[nodePool.NodeSelectorLabelKey] = nodePool.Name;
    }
}
