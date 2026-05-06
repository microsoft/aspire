// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Globalization;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a compute resource for Kubernetes.
/// </summary>
[AspireExport(ExposeProperties = true)]
public partial class KubernetesResource(string name, IResource resource, KubernetesEnvironmentResource kubernetesEnvironmentResource) : Resource(name), IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <inheritdoc/>
    public KubernetesEnvironmentResource Parent => kubernetesEnvironmentResource;

    internal void AddPrintSummaryStep()
    {
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var printResourceSummary = new PipelineStep
            {
                Name = $"print-{resource.Name}-summary",
                Description = $"Retrieves deployment status for {resource.Name}.",
                Action = async ctx => await HelmDeploymentEngine.PrintResourceSummaryAsync(ctx, kubernetesEnvironmentResource, resource, this).ConfigureAwait(false),
                Tags = [HelmDeploymentEngine.PrintSummaryTag],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            return new List<PipelineStep> { printResourceSummary };
        }));
    }

    internal record EndpointMapping(string Scheme, string Protocol, string Host, HelmValue Port, string Name, string? HelmExpression = null, HelmValue? ServicePort = null);
    internal Dictionary<string, EndpointMapping> EndpointMappings { get; } = [];
    internal Dictionary<string, HelmValue> EnvironmentVariables { get; } = [];
    internal Dictionary<string, HelmValue> Secrets { get; } = [];
    internal Dictionary<string, HelmValue> Parameters { get; } = [];
    internal Dictionary<string, HelmValue> AdditionalConfigValues { get; } = [];
    internal Dictionary<string, string> Labels { get; private set; } = [];
    internal List<string> Commands { get; } = [];
    internal List<VolumeMountV1> Volumes { get; } = [];
    internal List<PersistentVolume> PersistentVolumes { get; } = [];
    internal List<PersistentVolumeClaim> PersistentVolumeClaims { get; } = [];
#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    internal List<(ProbeType Type, ProbeV1 Probe)> Probes { get; } = [];
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    /// <summary>
    /// </summary>
    public Workload? Workload { get; set; }

    /// <summary>
    /// Gets or sets the Kubernetes ConfigMap associated with this resource.
    /// </summary>
    public ConfigMap? ConfigMap { get; set; }

    /// <summary>
    /// Gets or sets the Kubernetes Secret associated with this resource.
    /// </summary>
    public Secret? Secret { get; set; }

    /// <summary>
    /// Gets or sets the Kubernetes Service associated with this resource.
    /// </summary>
    public Service? Service { get; set; }

    /// <summary>
    /// Additional resources that are part of this Kubernetes service.
    /// </summary>
    public List<BaseKubernetesResource> AdditionalResources { get; } = [];

    /// <summary>
    /// Gets the resource that is the target of this Kubernetes service.
    /// </summary>
    internal IResource TargetResource => resource;

    internal IEnumerable<BaseKubernetesResource> GetTemplatedResources()
    {
        if (Workload is not null)
        {
            yield return Workload;
        }

        if (ConfigMap is not null)
        {
            yield return ConfigMap;
        }
        if (Secret is not null)
        {
            yield return Secret;
        }
        if (Service is not null)
        {
            yield return Service;
        }

        foreach (var volume in PersistentVolumes)
        {
            yield return volume;
        }

        foreach (var volumeClaim in PersistentVolumeClaims)
        {
            yield return volumeClaim;
        }

        foreach (var resource in AdditionalResources)
        {
            foreach (var label in Labels)
            {
                resource.Metadata.Labels.TryAdd(label.Key, label.Value);
            }

            yield return resource;
        }
    }

    private void BuildKubernetesResources()
    {
        SetLabels();
        CreateApplication();
        ConfigMap = resource.ToConfigMap(this);
        Secret = resource.ToSecret(this);
        Service = resource.ToService(this);
    }

    private void SetLabels()
    {
        Labels = new()
        {
            ["app.kubernetes.io/name"] = ".Chart.Name".ToHelmExpression(),
            ["app.kubernetes.io/component"] = resource.Name,
            ["app.kubernetes.io/instance"] = ".Release.Name".ToHelmExpression(),
        };
    }

    private void CreateApplication()
    {
        if (resource is IResourceWithConnectionString)
        {
            Workload = resource.ToStatefulSet(this);
            return;
        }

        Workload = resource.ToDeployment(this);
    }

    internal string GetContainerImageName(IResource resourceInstance)
    {
        if (!resourceInstance.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _) && resourceInstance is not ProjectResource)
        {
            if (resourceInstance.TryGetContainerImageName(out var containerImageName))
            {
                return containerImageName;
            }
        }

        var imageEnvName = $"{resourceInstance.Name.ToHelmValuesSectionName()}_image";
        var value = $"{resourceInstance.Name}:latest";
        var expression = new HelmValue(imageEnvName.ToHelmParameterExpression(resource.Name), value)
        {
            ImageResource = resourceInstance
        };

        Parameters[imageEnvName] = expression;
        return expression.ToScalar();
    }

    internal async Task ProcessResourceAsync(KubernetesEnvironmentContext context, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        ProcessEndpoints();
        ProcessVolumes();
        ProcessProbes();

        await ProcessEnvironmentAsync(context, executionContext, cancellationToken).ConfigureAwait(false);
        await ProcessArgumentsAsync(context, executionContext, cancellationToken).ConfigureAwait(false);

        BuildKubernetesResources();
    }

    private void ProcessEndpoints()
    {
        var resolvedEndpoints = resource.ResolveEndpoints(Parent.PortAllocator);

        foreach (var resolved in resolvedEndpoints)
        {
            var endpoint = resolved.Endpoint;

            if (resolved.TargetPort.Value is null)
            {
                // Default endpoint for ProjectResource - deployment tool assigns port.
                // Skip the default https endpoint — the container won't listen on HTTPS.
                // In Kubernetes, TLS termination is handled by ingress or service mesh.
                // We still create an EndpointMapping (needed for service discovery env vars)
                // but reuse the http endpoint's HelmValue so no duplicate K8s port is generated.
                // This matches the core framework's SetBothPortsEnvVariables() behavior,
                // which skips DefaultHttpsEndpoint when setting HTTPS_PORTS.
                // See: https://github.com/microsoft/aspire/issues/14029
                if (resource is ProjectResource projectResource &&
                    endpoint == projectResource.DefaultHttpsEndpoint)
                {
                    // Find the existing http endpoint's HelmValue to share it
                    var httpMapping = EndpointMappings.Values.FirstOrDefault(m => m.Scheme == "http");
                    if (httpMapping is not null)
                    {
                        EndpointMappings[endpoint.Name] = new(endpoint.UriScheme, GetKubernetesProtocolName(endpoint.Protocol), resource.Name.ToServiceName(), httpMapping.Port, endpoint.Name);
                        continue;
                    }
                }

                GenerateDefaultProjectEndpointMapping(endpoint);
                continue;
            }

            var portValue = resolved.TargetPort.Value.Value.ToString(CultureInfo.InvariantCulture);

            // Capture the exposed (service) port when it differs from the target (container) port.
            // This allows WithServicePort to set a different Kubernetes Service port than the container port.
            HelmValue? servicePort = null;
            if (resolved.ExposedPort.Value is int exposedPortValue &&
                exposedPortValue != resolved.TargetPort.Value.Value)
            {
                servicePort = HelmValue.Literal(exposedPortValue.ToString(CultureInfo.InvariantCulture));
            }

            EndpointMappings[endpoint.Name] = new(endpoint.UriScheme, GetKubernetesProtocolName(endpoint.Protocol), resource.Name.ToServiceName(), HelmValue.Literal(portValue), endpoint.Name, ServicePort: servicePort);
        }
    }

    private void GenerateDefaultProjectEndpointMapping(EndpointAnnotation endpoint)
    {
        const int defaultPort = 8080;

        // Create a Helm parameter for the container port
        var paramName = $"port_{endpoint.Name}".ToHelmValuesSectionName();
        var helmValue = new HelmValue(
            paramName.ToHelmParameterExpression(resource.Name),
            defaultPort
        );
        Parameters[paramName] = helmValue;
        EndpointMappings[endpoint.Name] = new(endpoint.UriScheme, GetKubernetesProtocolName(endpoint.Protocol), resource.Name.ToServiceName(), helmValue, endpoint.Name);
    }

    private void ProcessVolumes()
    {
        if (!resource.TryGetContainerMounts(out var mounts))
        {
            return;
        }

        foreach (var volume in mounts)
        {
            if (volume.Source is null || volume.Target is null)
            {
                throw new InvalidOperationException("Volume source and target must be set");
            }

            if (volume.Type == ContainerMountType.BindMount)
            {
                throw new InvalidOperationException("Bind mounts are not supported by the Kubernetes publisher");
            }

            var newVolume = new VolumeMountV1
            {
                Name = volume.Source,
                ReadOnly = volume.IsReadOnly,
                MountPath = volume.Target,
            };

            Volumes.Add(newVolume);
        }
    }

#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    private void ProcessProbes()
    {
        if (!resource.TryGetAnnotationsOfType<ProbeAnnotation>(out var probeAnnotations))
        {
            return;
        }

        foreach (var probeAnnotation in probeAnnotations)
        {
            ProbeV1? probe = null;
            if (probeAnnotation is EndpointProbeAnnotation endpointProbeAnnotation
                && EndpointMappings.TryGetValue(endpointProbeAnnotation.EndpointReference.EndpointName, out var endpointMapping))
            {
                probe = new ProbeV1()
                {
                    HttpGet = new()
                    {
                        Path = endpointProbeAnnotation.Path,
                        Port = GetEndpointValue(endpointMapping, EndpointProperty.TargetPort),
                        Scheme = endpointMapping.Scheme,
                    },
                };
            }

            if (probe is not null)
            {
                probe.InitialDelaySeconds = probeAnnotation.InitialDelaySeconds;
                probe.PeriodSeconds = probeAnnotation.PeriodSeconds;
                probe.TimeoutSeconds = probeAnnotation.TimeoutSeconds;
                probe.FailureThreshold = probeAnnotation.FailureThreshold;
                probe.SuccessThreshold = probeAnnotation.SuccessThreshold;

                Probes.Add((probeAnnotation.Type, probe));
            }
        }
    }
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    private async Task ProcessArgumentsAsync(KubernetesEnvironmentContext environmentContext, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var commandLineArgsCallbackAnnotations))
        {
            var context = new CommandLineArgsCallbackContext([], resource, cancellationToken: cancellationToken)
            {
                ExecutionContext = executionContext
            };

            foreach (var c in commandLineArgsCallbackAnnotations)
            {
                await c.Callback(context).ConfigureAwait(false);
            }

            foreach (var arg in context.Args)
            {
                var value = await ProcessValueAsync(environmentContext, executionContext, arg).ConfigureAwait(false);

                if (value is not string str)
                {
                    throw new NotSupportedException("Command line args must be strings");
                }

                Commands.Add(new(str));
            }
        }
    }

    private async Task ProcessEnvironmentAsync(KubernetesEnvironmentContext environmentContext, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var environmentCallbacks))
        {
            var context = new EnvironmentCallbackContext(executionContext, resource, cancellationToken: cancellationToken);

            foreach (var c in environmentCallbacks)
            {
                await c.Callback(context).ConfigureAwait(false);
            }

            // Remove HTTPS service discovery variables — containers in Kubernetes don't have TLS certificates.
            // TLS termination is handled externally by ingress controllers or service mesh.
            // This matches the Docker Compose behavior in RemoveHttpsServiceDiscoveryVariables.
            RemoveHttpsServiceDiscoveryVariables(context.EnvironmentVariables);

            foreach (var environmentVariable in context.EnvironmentVariables)
            {
                var key = environmentVariable.Key;

                // Check if the value contains deferred providers (e.g., Bicep outputs)
                // that can only be resolved after infrastructure provisioning.
                // If so, create a deferred HelmValue with the env var key name.
                if (IsUnresolvedAtPublishTime(environmentVariable.Value) &&
                    environmentVariable.Value is IValueProvider deferredVp)
                {
                    var deferredHelmValue = CreateDeferredHelmValue(key, deferredVp);
                    ProcessEnvironmentHelmExpression(deferredHelmValue, key);
                    continue;
                }

                var value = await ProcessValueAsync(environmentContext, executionContext, environmentVariable.Value).ConfigureAwait(false);

                switch (value)
                {
                    case HelmValue helmExpression:
                        ProcessEnvironmentHelmExpression(helmExpression, key);
                        continue;
                    case string stringValue:
                        ProcessEnvironmentStringValue(stringValue, key, resource.Name);
                        continue;
                    default:
                        ProcessEnvironmentDefaultValue(value, key, resource.Name);
                        break;
                }
            }
        }
    }

    private void ProcessEnvironmentHelmExpression(HelmValue helmExpression, string key)
    {
        switch (helmExpression)
        {
            case { ExpressionContainsHelmSecretExpression: true, ValueContainsSecretValuesExpression: false }:
                Secrets[key] = helmExpression;
                return;
            case { ExpressionContainsHelmSecretExpression: false, ValueContainsSecretValuesExpression: false }:
                EnvironmentVariables[key] = helmExpression;
                break;
        }
    }

    private void ProcessEnvironmentStringValue(string stringValue, string key, string resourceName)
    {
        if (stringValue.ContainsHelmValuesSecretExpression())
        {
            var secretExpression = stringValue.ToHelmSecretExpression(resourceName);
            Secrets[key] = new(secretExpression, stringValue);
            return;
        }

        var configExpression = key.ToHelmConfigExpression(resourceName);
        EnvironmentVariables[key] = new(configExpression, stringValue);
    }

    private void ProcessEnvironmentDefaultValue(object value, string key, string resourceName)
    {
        var configExpression = key.ToHelmConfigExpression(resourceName);
        EnvironmentVariables[key] = new(configExpression, value.ToString() ?? string.Empty);
    }

    private static void RemoveHttpsServiceDiscoveryVariables(Dictionary<string, object> environmentVariables)
    {
        var keysToRemove = environmentVariables
            .Where(kvp => kvp.Value is EndpointReference epRef && epRef.Scheme == "https" && kvp.Key.StartsWith("services__"))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            environmentVariables.Remove(key);
        }
    }

    private async Task<object> ProcessValueAsync(KubernetesEnvironmentContext context, DistributedApplicationExecutionContext executionContext, object value, bool embedded = false)
    {
        while (true)
        {
            if (value is string s)
            {
                return s;
            }

            if (value is EndpointReference ep)
            {
                var referencedResource = ep.Resource == this
                    ? this
                    : await context.CreateKubernetesResourceAsync(ep.Resource, executionContext, default).ConfigureAwait(false);

                var mapping = referencedResource.EndpointMappings[ep.EndpointName];

                var url = GetEndpointValue(mapping, EndpointProperty.Url);

                return url;
            }

            if (value is ParameterResource param)
            {
                return AllocateParameter(param, TargetResource);
            }

            if (value is ConnectionStringReference cs)
            {
                value = cs.Resource.ConnectionStringExpression;
                continue;
            }

            if (value is IResourceWithConnectionString csrs)
            {
                value = csrs.ConnectionStringExpression;
                continue;
            }

            if (value is EndpointReferenceExpression epExpr)
            {
                var referencedResource = epExpr.Endpoint.Resource == this
                    ? this
                    : await context.CreateKubernetesResourceAsync(epExpr.Endpoint.Resource, executionContext, default).ConfigureAwait(false);

                var mapping = referencedResource.EndpointMappings[epExpr.Endpoint.EndpointName];

                var val = GetEndpointValue(mapping, epExpr.Property, embedded && epExpr.Property is EndpointProperty.Port or EndpointProperty.TargetPort);

                return val;
            }

            if (value is ReferenceExpression expr)
            {
                if (expr.IsConditional)
                {
                    // When the condition is a parameter, use Helm flow control to defer
                    // evaluation to helm install/upgrade time.
                    if (expr.Condition is ParameterResource conditionParam)
                    {
                        return await BuildHelmConditional(context, executionContext, expr, conditionParam, embedded).ConfigureAwait(false);
                    }

                    // For non-parameter conditions, resolve statically at generation time.
                    var conditionContext = new ValueProviderContext { ExecutionContext = executionContext };
                    var conditionStr = await expr.Condition!.GetValueAsync(conditionContext, default).ConfigureAwait(false);

                    var branch = string.Equals(conditionStr, expr.MatchValue, StringComparison.OrdinalIgnoreCase)
                        ? expr.WhenTrue!
                        : expr.WhenFalse!;
                    return await ProcessValueAsync(context, executionContext, branch, embedded).ConfigureAwait(false);
                }

                if (expr is { Format: "{0}", ValueProviders.Count: 1 })
                {
                    var inner = await ProcessValueAsync(context, executionContext, expr.ValueProviders[0], embedded).ConfigureAwait(false);
                    // When embedded in a larger format string, convert to string for interpolation.
                    // When at the top level, preserve the original object (e.g., HelmValue with ValuesKey)
                    // so ProcessEnvironmentHelmExpression handles it correctly.
                    return embedded ? inner?.ToString() ?? string.Empty : inner;
                }

                var args = new object[expr.ValueProviders.Count];
                var index = 0;

                foreach (var vp in expr.ValueProviders)
                {
                    var val = await ProcessValueAsync(context, executionContext, vp, true).ConfigureAwait(false);
                    args[index++] = val ?? throw new InvalidOperationException("Value is null");
                }

                return string.Format(CultureInfo.InvariantCulture, expr.Format, args);
            }

            // If we don't know how to process the value, we just return it as an external reference
            if (value is IManifestExpressionProvider r)
            {
                context.Logger.NotSupportedResourceWarning(nameof(value), r.GetType().Name);

                return ResolveUnknownValue(r, TargetResource);
            }

            throw new NotSupportedException($"Unsupported value type: {value.GetType().Name}");
        }
    }

    private async Task<object> BuildHelmConditional(KubernetesEnvironmentContext context, DistributedApplicationExecutionContext executionContext, ReferenceExpression expr, ParameterResource conditionParam, bool embedded)
    {
        // Process both branches to get their rendered values.
        var whenTrueResult = await ProcessValueAsync(context, executionContext, expr.WhenTrue!, embedded).ConfigureAwait(false);
        var whenFalseResult = await ProcessValueAsync(context, executionContext, expr.WhenFalse!, embedded).ConfigureAwait(false);

        var whenTrueStr = whenTrueResult.ToString() ?? string.Empty;
        var whenFalseStr = whenFalseResult.ToString() ?? string.Empty;

        // Allocate the condition parameter into values.yaml under the parameters section.
        var formattedName = conditionParam.Name.ToHelmValuesSectionName();
        var paramExpression = formattedName.ToHelmParameterExpression(TargetResource.Name);

        if (!Parameters.ContainsKey(formattedName))
        {
            Parameters[formattedName] = conditionParam.Default is null || conditionParam.Secret
                ? new HelmValue(paramExpression, (string?)null)
                : new HelmValue(paramExpression, conditionParam);
        }

        // Ensure parameter values referenced in branches are populated in values.yaml.
        AllocateBranchParameters(expr.WhenTrue!);
        AllocateBranchParameters(expr.WhenFalse!);

        // Extract the values path (e.g., .Values.parameters.myapp.enable_tls) from {{ expression }}.
        // Pipe through | lower for case-insensitive comparison, matching .NET's
        // StringComparison.OrdinalIgnoreCase used in other execution/publish paths.
        var conditionPath = $"({HelmExtensions.ScalarExpressionPattern().Match(paramExpression).Value.Trim()} | lower)";
        var escapedMatch = (expr.MatchValue ?? string.Empty).ToLowerInvariant().Replace("\\", "\\\\").Replace("\"", "\\\"");

        var ifElseExpression = $"{{{{ if eq {conditionPath} \"{escapedMatch}\" }}}}{whenTrueStr}{{{{ else }}}}{whenFalseStr}{{{{ end }}}}";
        return HelmValue.Literal(ifElseExpression);
    }

    /// <summary>
    /// Ensures that any <see cref="ParameterResource"/> instances referenced in a branch's
    /// value providers are allocated in the appropriate dictionary (EnvironmentVariables or
    /// Secrets) so their values flow to values.yaml via <c>AddValuesToHelmSectionAsync</c>.
    /// </summary>
    private void AllocateBranchParameters(ReferenceExpression branch)
    {
        foreach (var vp in branch.ValueProviders)
        {
            if (vp is ParameterResource branchParam)
            {
                var helmValue = AllocateParameter(branchParam, TargetResource);
                var key = branchParam.Name.ToHelmValuesSectionName();

                // Store in AdditionalConfigValues rather than EnvironmentVariables to avoid
                // case-insensitive key collisions in ToConfigMap's processedKeys. These values
                // flow to the config section of values.yaml but do not appear as env vars.
                if (helmValue.ExpressionContainsHelmSecretExpression)
                {
                    Secrets.TryAdd(key, helmValue);
                }
                else
                {
                    AdditionalConfigValues.TryAdd(key, helmValue);
                }
            }
        }
    }

    private static string GetEndpointValue(EndpointMapping mapping, EndpointProperty property, bool embedded = false)
    {
        var (scheme, _, host, port, _, _, _) = mapping;

        return property switch
        {
            EndpointProperty.Url => GetHostValue($"{scheme}://", suffix: GetPortSuffix()),
            EndpointProperty.Host or EndpointProperty.IPV4Host => GetHostValue(),
            EndpointProperty.Port => GetPort(),
            EndpointProperty.HostAndPort => GetHostValue(suffix: GetPortSuffix()),
            EndpointProperty.TargetPort => GetPort(),
            EndpointProperty.Scheme => scheme,
            _ => throw new NotSupportedException(),
        };

        string GetHostValue(string? prefix = null, string? suffix = null)
        {
            return $"{prefix}{host}{suffix}";
        }

        string GetPort()
        {
            var rawPort = embedded ? port.Expression ?? port.ValueString : port.ToScalar();

            return string.IsNullOrWhiteSpace(rawPort)
                ? string.Empty
                : rawPort;
        }

        string GetPortSuffix()
        {
            var portValue = port switch
            {
                _ when !string.IsNullOrWhiteSpace(port.Expression)
                  => port.Expression,
                { ValueString: { } } => port.ValueString,
                _ => null
            };

            return string.IsNullOrWhiteSpace(portValue)
                 ? string.Empty
                 : $":{portValue}";
        }
    }

    private static HelmValue AllocateParameter(ParameterResource parameter, IResource resource)
    {
        var formattedName = parameter.Name.ToHelmValuesSectionName();

        var expression = parameter.Secret ?
            formattedName.ToHelmSecretExpression(resource.Name) :
            formattedName.ToHelmConfigExpression(resource.Name);

        // Always store the parameter reference for deferred resolution.
        // Secrets and parameters without defaults are resolved at deploy time (not publish time).
        // ValuesKey preserves the parameter name so values.yaml key matches the Helm expression path.
        return new(expression, parameter) { ValuesKey = formattedName };
    }
    
    private static HelmValue ResolveUnknownValue(IManifestExpressionProvider parameter, IResource resource)
    {
        var formattedName = parameter.ValueExpression.Replace(HelmExtensions.StartDelimiter, string.Empty)
            .Replace(HelmExtensions.EndDelimiter, string.Empty)
            .Replace("{", string.Empty)
            .Replace("}", string.Empty)
            .Replace(".", "_")
            .ToHelmValuesSectionName();

        var helmExpression = parameter.ValueExpression.ContainsHelmValuesSecretExpression() ?
            formattedName.ToHelmSecretExpression(resource.Name) :
            formattedName.ToHelmConfigExpression(resource.Name);

        var helmValue = new HelmValue(helmExpression, parameter.ValueExpression)
        {
            // If the expression provider also implements IValueProvider, attach it
            // for deploy-time resolution. This handles Bicep output references,
            // connection strings, and any other deferred value source.
            ValueProviderSource = parameter as IValueProvider
        };

        return helmValue;
    }

    /// <summary>
    /// Checks if a value contains sub-expressions that cannot be resolved
    /// at publish time and need deploy-time resolution via IValueProvider.
    /// Recursively checks ReferenceExpression value providers.
    /// </summary>
    private static bool IsUnresolvedAtPublishTime(object value)
    {
        return value switch
        {
            string => false,
            EndpointReference => false,
            EndpointReferenceExpression => false,
            ParameterResource => false,
            ConnectionStringReference cs => IsUnresolvedAtPublishTime(cs.Resource.ConnectionStringExpression),
            IResourceWithConnectionString csrs => IsUnresolvedAtPublishTime(csrs.ConnectionStringExpression),
            ReferenceExpression expr => expr.ValueProviders.Any(IsUnresolvedAtPublishTime),
            // Any other IManifestExpressionProvider that also implements IValueProvider
            // is a deferred source (e.g., BicepOutputReference)
            IManifestExpressionProvider when value is IValueProvider => true,
            _ => false
        };
    }

    /// <summary>
    /// Creates a HelmValue that defers resolution to deploy time via IValueProvider.
    /// </summary>
    /// <param name="key">The environment variable or config key name to use as the Helm values path.</param>
    /// <param name="valueProvider">The value provider for deploy-time resolution.</param>
    private HelmValue CreateDeferredHelmValue(string key, IValueProvider valueProvider)
    {
        var helmExpression = key.ToHelmConfigExpression(TargetResource.Name);
        return new HelmValue(helmExpression, string.Empty)
        {
            ValueProviderSource = valueProvider
        };
    }

    private static string GetKubernetesProtocolName(ProtocolType type)
        => type switch
        {
            ProtocolType.Tcp => "TCP",
            ProtocolType.Udp => "UDP",
            _ => throw new InvalidOperationException($"Unsupported protocol type: {type}"),
        };

    /// <summary>
    /// Represents a Helm value, which can be either a literal value, a Helm expression, or a helm expression with a known value. 
    /// </summary>
    internal class HelmValue
    {
        private HelmValue(object? value)
        {
            Value = value;
        }

        /// <summary>
        /// Initializes a new instance of the HelmValue class with the specified expression and value.
        /// </summary>
        /// <param name="expression">The Helm expression associated with the value. Cannot be null.</param>
        /// <param name="value">The value to assign. Can be null.</param>
        public HelmValue(string expression, object? value)
        {
            Expression = expression;
            Value = value;
            ValueType = value?.GetType();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="HelmValue"/> class with a Helm expression and a parameter source. 
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="parameterSource"></param>
        public HelmValue(string expression, ParameterResource parameterSource)
        {
            Expression = expression;
            ParameterSource = parameterSource;
        }

        /// <summary>
        /// Gets the Helm expression associated with this HelmValue, if any. 
        /// </summary>
        public string? Expression { get; }

        /// <summary>
        /// Gets the value associated with this HelmValue, if any.
        /// </summary>
        public object? Value { get; }

        /// <summary>
        /// Gets the type of the value associated with this HelmValue, if any. 
        /// </summary>
        protected Type? ValueType { get; }

        /// <summary>
        /// Gets the string representation of the value, if available. 
        /// </summary>
        public string? ValueString => Value?.ToString();

        /// <summary>
        /// Gets the parameter resource associated with this HelmValue, if any.
        /// </summary>
        public ParameterResource? ParameterSource { get; }

        /// <summary>
        /// Gets the resource associated with a container image reference, if any.
        /// When set, the image name is resolved at deploy time via <see cref="ContainerImageReference"/>
        /// to include the container registry prefix.
        /// </summary>
        public IResource? ImageResource { get; init; }

        /// <summary>
        /// Gets the value provider for deferred resolution at deploy time.
        /// When set, the value is resolved via <see cref="IValueProvider"/>
        /// during the prepare step, replacing the placeholder in values.yaml.
        /// This handles any value provider (e.g., Bicep output references, connection strings).
        /// </summary>
        public IValueProvider? ValueProviderSource { get; init; }

        /// <summary>
        /// Gets the key to use when writing this value to the Helm values.yaml file.
        /// When set, this overrides the dictionary key to ensure the values.yaml key matches
        /// the Helm expression path (e.g., parameter name "cache_password" vs env var name "REDIS_PASSWORD").
        /// </summary>
        public string? ValuesKey { get; init; }

        /// <summary>
        /// Indicates whether the expression contains a Helm secret expression. 
        /// </summary>
        public bool ExpressionContainsHelmSecretExpression
            => Expression?.ContainsHelmValuesSecretExpression() ?? false;

        /// <summary>
        /// Gets a value indicating whether the value string contains any secret value expressions.
        /// </summary>
        public bool ValueContainsSecretValuesExpression
            => ValueString?.ContainsHelmValuesSecretExpression() ?? false;

        /// <summary>
        /// Gets a value indicating whether the current value string contains a Helm values expression.
        /// </summary>
        public bool ValueContainsHelmExpression
            => ValueString?.ContainsHelmValuesExpression() ?? false;

        /// <summary>
        /// Returns a string representation of the HelmValue.
        /// </summary>
        /// <returns>A string that represents the value or expression, or an empty string if neither is set.</returns>
        public override string ToString()
        {
            return ValueString ?? Expression ?? string.Empty;
        }

        /// <summary>
        /// Converts the HelmValue to a scalar value or expression, applying type conversions if necessary. 
        /// </summary>
        /// <returns>
        /// A string representing the scalar value or Helm expression.
        /// </returns>
        public string ToScalar()
        {
            if (string.IsNullOrWhiteSpace(Expression))
            {
                return ValueString ?? string.Empty;
            }

            var expression = HelmExtensions.ScalarExpressionPattern().Match(Expression);
            if (expression is not { Success: true } or not { Captures.Count: > 0 })
            {
                // if its not a scalar expression, use `ToString`
                return ToString();
            }

            var typeConversion = ValueType switch
            {
                var t when t == typeof(int) => $" {HelmExtensions.PipelineDelimiter} int",
                var t when t == typeof(long) => $" {HelmExtensions.PipelineDelimiter} int64",
                var t when t == typeof(float)
                        || t == typeof(double)
                        || t == typeof(decimal) => $" {HelmExtensions.PipelineDelimiter} float64",
                _ => string.Empty
            };

            return $"{expression.Captures[0].Value.Trim()}{typeConversion}".ToHelmExpression();
        }

        public static HelmValue Literal(object value) => new(value);

    }
}
