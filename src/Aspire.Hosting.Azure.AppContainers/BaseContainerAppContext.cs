// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Aspire.Hosting.Azure.Utils;

namespace Aspire.Hosting.Azure;

internal abstract class BaseContainerAppContext(IResource resource, ContainerAppEnvironmentContext containerAppEnvironmentContext)
{
    protected readonly ContainerAppEnvironmentContext _containerAppEnvironmentContext = containerAppEnvironmentContext;

    public IResource Resource => resource;

    /// <summary>
    /// The normalized container app name (lowercase) that will be used consistently 
    /// throughout the container app creation process for both the resource identifier 
    /// and endpoint mapping host names.
    /// </summary>
    public string NormalizedContainerAppName => resource.Name.ToLowerInvariant();

    protected record struct EndpointMapping(string Scheme, string Host, int Port, int? TargetPort, bool IsHttpIngress, bool External, bool TlsEnabled);
    protected readonly Dictionary<string, EndpointMapping> _endpointMapping = [];

    // Resolved environment variables and command line args
    // These contain the values that need to be further transformed into
    // bicep compatible values
    public Dictionary<string, object> EnvironmentVariables { get; } = [];
    public List<object> Args { get; } = [];
    public Dictionary<string, (ContainerMountAnnotation, BicepOutputReference)> Volumes { get; } = [];

    // Bicep build state
    protected ProvisioningParameter? _containerRegistryUrlParameter;
    protected ProvisioningParameter? _containerRegistryManagedIdentityIdParameter;

    protected AzureResourceInfrastructure? _infrastructure;
    public AzureResourceInfrastructure Infra => _infrastructure ?? throw new InvalidOperationException("Infra is not set");

    public abstract void BuildContainerApp(AzureResourceInfrastructure infra);

    protected void AddVolumes(BicepList<ContainerAppVolume> volumes, ContainerAppContainer containerAppContainer)
    {
        foreach (var (volumeName, (volume, storageName)) in Volumes)
        {
            var containerAppVolume = new ContainerAppVolume
            {
                Name = volumeName,
                StorageType = ContainerAppStorageType.AzureFile,
                StorageName = storageName.AsProvisioningParameter(Infra),
            };

            volumes.Add(containerAppVolume);

            var containerAppVolumeMount = new ContainerAppVolumeMount
            {
                VolumeName = volumeName,
                MountPath = volume.Target,
            };

            containerAppContainer.VolumeMounts.Add(containerAppVolumeMount);
        }
    }

    protected void AddAzureClientId(IAppIdentityResource? appIdentityResource, BicepList<ContainerAppEnvironmentVariable> env)
    {
        if (appIdentityResource is not null)
        {
            env.Add(new ContainerAppEnvironmentVariable
            {
                Name = "AZURE_CLIENT_ID",
                Value = AllocateParameter(appIdentityResource.ClientId)
            });

            // DefaultAzureCredential should only use ManagedIdentityCredential when running in Azure
            env.Add(new ContainerAppEnvironmentVariable
            {
                Name = "AZURE_TOKEN_CREDENTIALS",
                Value = "ManagedIdentityCredential"
            });
        }
    }

    protected static bool TryGetContainerImageName(IResource resource, out string? containerImageName)
    {
        // If the resource has a Dockerfile build annotation, we don't have the image name
        // it will come as a parameter
        if (resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out _))
        {
            containerImageName = null;
            return false;
        }

        return resource.TryGetContainerImageName(out containerImageName);
    }

    public async Task ProcessResourceAsync(CancellationToken cancellationToken)
    {
        ProcessEndpoints();
        ProcessVolumes();

        await ProcessEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        await ProcessArgumentsAsync(cancellationToken).ConfigureAwait(false);
    }

    protected virtual void ProcessEndpoints() { }

    private async Task ProcessArgumentsAsync(CancellationToken cancellationToken)
    {
        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var commandLineArgsCallbackAnnotations))
        {
            var context = new CommandLineArgsCallbackContext(Args, resource, cancellationToken: cancellationToken)
            {
                ExecutionContext = _containerAppEnvironmentContext.ExecutionContext,
            };

            foreach (var c in commandLineArgsCallbackAnnotations)
            {
                await c.Callback(context).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var environmentCallbacks))
        {
            var context = new EnvironmentCallbackContext(_containerAppEnvironmentContext.ExecutionContext, resource, EnvironmentVariables, cancellationToken: cancellationToken);

            foreach (var c in environmentCallbacks)
            {
                await c.Callback(context).ConfigureAwait(false);
            }
        }
    }

    private static BicepValue<string> ResolveValue(object val)
    {
        return val switch
        {
            BicepValue<string> s => s,
            string s => s,
            ProvisioningParameter p => p,
            FormattableString fs => BicepFunction.Interpolate(fs),
            _ => throw new NotSupportedException("Unsupported value type " + val.GetType())
        };
    }

    private void ProcessVolumes()
    {
        if (resource.TryGetContainerMounts(out var mounts))
        {
            var bindMountIndex = 0;
            var volumeIndex = 0;

            foreach (var volume in mounts)
            {
                var (index, volumeName) = volume.Type switch
                {
                    ContainerMountType.BindMount => (bindMountIndex, $"bm{bindMountIndex}"),
                    ContainerMountType.Volume => (volumeIndex, $"v{volumeIndex}"),
                    _ => throw new NotSupportedException()
                };

                var storageName = _containerAppEnvironmentContext.Environment.GetVolumeStorage(resource, volume, index);

                Volumes[volumeName] = (volume, storageName);

                if (volume.Type == ContainerMountType.BindMount)
                {
                    bindMountIndex++;
                }
                else
                {
                    volumeIndex++;
                }

            }
        }
    }

    private BicepValue<string> GetEndpointValue(EndpointMapping mapping, EndpointProperty property)
    {
        var (scheme, host, port, targetPort, isHttpIngress, external, tlsEnabled) = mapping;

        BicepValue<string> GetHostValue(string? prefix = null, string? suffix = null)
        {
            if (isHttpIngress)
            {
                var domain = AllocateParameter(_containerAppEnvironmentContext.Environment.ContainerAppDomain);

                return external ? BicepFunction.Interpolate($$"""{{prefix}}{{host}}.{{domain}}{{suffix}}""") : BicepFunction.Interpolate($$"""{{prefix}}{{host}}.internal.{{domain}}{{suffix}}""");
            }

            return $"{prefix}{host}{suffix}";
        }

        return property switch
        {
            EndpointProperty.Url => GetHostValue($"{scheme}://", suffix: isHttpIngress ? null : $":{port}"),
            EndpointProperty.Host or EndpointProperty.IPV4Host => GetHostValue(),
            EndpointProperty.Port => port.ToString(CultureInfo.InvariantCulture),
            EndpointProperty.HostAndPort => GetHostValue(suffix: $":{port}"),
            EndpointProperty.TargetPort => targetPort is null ? AllocateContainerPortParameter() : $"{targetPort}",
            EndpointProperty.Scheme => scheme,
            EndpointProperty.TlsEnabled => tlsEnabled ? bool.TrueString : bool.FalseString,
            _ => throw new NotSupportedException(),
        };
    }

    private (object, SecretType) ProcessValue(object value, SecretType secretType = SecretType.None, object? parent = null)
    {
        if (value is string s)
        {
            return (s, secretType);
        }

        if (value is EndpointReference ep)
        {
            var context = ep.Resource == resource
                ? this
                : _containerAppEnvironmentContext.GetContainerAppContext(ep.Resource);

            var mapping = context._endpointMapping[ep.EndpointName];

            var url = GetEndpointValue(mapping, EndpointProperty.Url);

            return (url, secretType);
        }

        if (value is ParameterResource param)
        {
            var st = param.Secret ? SecretType.Normal : secretType;

            return (AllocateParameter(param, secretType: st), st);
        }

        if (value is ConnectionStringReference cs)
        {
            return ProcessValue(cs.Resource.ConnectionStringExpression, secretType: secretType, parent: parent);
        }

        if (value is IResourceWithConnectionString csrs)
        {
            return ProcessValue(csrs.ConnectionStringExpression, secretType: secretType, parent: parent);
        }

        if (value is BicepOutputReference output)
        {
            return (AllocateParameter(output, secretType: secretType), secretType);
        }

#pragma warning disable CS0618 // Type or member is obsolete
        if (value is BicepSecretOutputReference)
        {
            throw new NotSupportedException("Automatic Key vault generation is not supported in this environment. Please create a key vault resource directly.");
        }
#pragma warning restore CS0618 // Type or member is obsolete

        if (value is IAzureKeyVaultSecretReference vaultSecretReference)
        {
            if (parent is null)
            {
                return (AllocateKeyVaultSecretUriReference(vaultSecretReference), SecretType.KeyVault);
            }

            return (AllocateParameter(vaultSecretReference, secretType: SecretType.KeyVault), SecretType.KeyVault);
        }

        if (value is EndpointReferenceExpression epExpr)
        {
            var context = epExpr.Endpoint.Resource == resource
                ? this
                : _containerAppEnvironmentContext.GetContainerAppContext(epExpr.Endpoint.Resource);

            var mapping = context._endpointMapping[epExpr.Endpoint.EndpointName];

            var val = GetEndpointValue(mapping, epExpr.Property);

            return (val, secretType);
        }

        if (value is ReferenceExpression expr)
        {
            // Handle conditional expressions
            if (expr.IsConditional)
            {
                var (conditionVal, _) = ProcessValue(expr.Condition!, secretType, parent: expr);

                // If the condition resolves to a static string, evaluate at publish time
                string? staticCondition = conditionVal is string str ? str : null;
                if (staticCondition is null && conditionVal is BicepValue<string> bv
                    && bv.Compile() is StringLiteralExpression sle)
                {
                    staticCondition = sle.Value;
                }

                if (staticCondition is not null)
                {
                    var branch = string.Equals(staticCondition, expr.MatchValue, StringComparison.OrdinalIgnoreCase)
                        ? expr.WhenTrue!
                        : expr.WhenFalse!;
                    return ProcessValue(branch, secretType, parent: parent);
                }

                // Condition is a Bicep parameter/output — emit a ternary expression
                var (whenTrueVal, trueSecret) = ProcessValue(expr.WhenTrue!, secretType, parent: expr);
                var (whenFalseVal, falseSecret) = ProcessValue(expr.WhenFalse!, secretType, parent: expr);

                var conditional = new ConditionalExpression(
                    new BinaryExpression(BicepFunction.ToLower(ResolveValue(conditionVal).Compile()).Compile(), BinaryBicepOperator.Equal, new StringLiteralExpression((expr.MatchValue ?? string.Empty).ToLowerInvariant())),
                    ResolveValue(whenTrueVal).Compile(),
                    ResolveValue(whenFalseVal).Compile());

                var finalSecret = trueSecret != SecretType.None || falseSecret != SecretType.None
                    ? SecretType.Normal
                    : SecretType.None;

                return (new BicepValue<string>(conditional), finalSecret);
            }

            // Special case simple expressions
            if (expr.Format == "{0}" && expr.ValueProviders.Count == 1)
            {
                var val = ProcessValue(expr.ValueProviders[0], secretType, parent: parent);

                if (expr.StringFormats[0] is string format)
                {
                    val = (BicepFormattingHelpers.FormatBicepExpression(val, format), secretType);
                }

                return val;
            }

            var args = new object[expr.ValueProviders.Count];
            var index = 0;
            var finalSecretType = SecretType.None;

            foreach (var vp in expr.ValueProviders)
            {
                var (val, secret) = ProcessValue(vp, secretType, parent: expr);

                if (secret != SecretType.None)
                {
                    finalSecretType = SecretType.Normal;
                }

                if (expr.StringFormats[index] is string format)
                {
                    val = BicepFormattingHelpers.FormatBicepExpression(val, format);
                }

                args[index++] = val;
            }

            return (FormattableStringFactory.Create(expr.Format, args), finalSecretType);
        }

        if (value is IManifestExpressionProvider manifestExpressionProvider)
        {
            return (AllocateParameter(manifestExpressionProvider, secretType), secretType);
        }

        throw new NotSupportedException("Unsupported value type " + value.GetType());
    }

    private BicepValue<string> AllocateKeyVaultSecretUriReference(IAzureKeyVaultSecretReference secretOutputReference)
    {
        var secret = secretOutputReference.AsKeyVaultSecret(Infra);

        return secret.Properties.SecretUri;
    }

    protected ProvisioningParameter AllocateContainerImageParameter()
        => AllocateParameter(new ContainerImageReference(resource));

    protected BicepValue<string> AllocateContainerPortParameter()
        => AllocateParameter(new ContainerPortReference(resource));

    protected static BicepValue<int> AsInt(BicepValue<string> value)
    {
        return new FunctionCallExpression(new IdentifierExpression("int"), value.Compile());
    }

    protected void AllocateContainerRegistryParameters()
    {
        _containerRegistryUrlParameter ??= AllocateParameter(_containerAppEnvironmentContext.Environment.ContainerRegistryUrl);
        _containerRegistryManagedIdentityIdParameter ??= AllocateParameter(_containerAppEnvironmentContext.Environment.ContainerRegistryManagedIdentityId);
    }

    protected ProvisioningParameter AllocateParameter(IManifestExpressionProvider parameter, SecretType secretType = SecretType.None)
    {
        return parameter.AsProvisioningParameter(Infra, isSecure: secretType != SecretType.None);
    }

    protected void SetEntryPoint(ContainerAppContainer container)
    {
        if (Resource is ContainerResource containerResource && containerResource.Entrypoint is { } entrypoint)
        {
            container.Command = [entrypoint];
        }
    }

    protected void AddEnvironmentVariablesAndCommandLineArgs(
        ContainerAppContainer container,
        Func<BicepList<ContainerAppWritableSecret>> getContainerAppConfigurationSecrets,
        BicepValue<string>? containerAppIdentityId)
    {
        if (EnvironmentVariables.Count > 0)
        {
            container.Env = [];

            foreach (var kv in EnvironmentVariables)
            {
                var (val, secretType) = ProcessValue(kv.Value);

                var argValue = ResolveValue(val);

                if (secretType != SecretType.None)
                {
                    var secretName = kv.Key.Replace("_", "-").ToLowerInvariant();

                    var secrets = getContainerAppConfigurationSecrets();

                    // Get or add the secret
                    var secret = secrets.FirstOrDefault(s => s.Value?.Name.Value == secretName)
                                    ?.Value;

                    if (secret is null)
                    {
                        secret = new ContainerAppWritableSecret()
                        {
                            Name = secretName
                        };

                        if (secretType == SecretType.KeyVault)
                        {
                            // TODO: this should be able to use ToUri(), but it hit an issue
                            secret.KeyVaultUri = new BicepValue<Uri>(((BicepExpression?)argValue)!);

                            if (containerAppIdentityId is not null)
                            {
                                secret.Identity = containerAppIdentityId;
                            }
                        }
                        else
                        {
                            secret.Value = argValue;
                        }

                        secrets.Add(secret);
                    }

                    // The value is the secret name
                    val = secretName;
                }

                container.Env.Add(secretType switch
                {
                    SecretType.None => new ContainerAppEnvironmentVariable { Name = kv.Key, Value = argValue },
                    SecretType.Normal or SecretType.KeyVault => new ContainerAppEnvironmentVariable { Name = kv.Key, SecretRef = (string)val },
                    _ => throw new NotSupportedException()
                });
            }
        }

        if (Args.Count > 0)
        {
            container.Args = [];

            foreach (var arg in Args)
            {
                var (val, _) = ProcessValue(arg);

                var argValue = ResolveValue(val);

                container.Args.Add(argValue);
            }
        }
    }

    protected void AddContainerRegistryManagedIdentity(ManagedServiceIdentity identity)
    {
        if (_containerRegistryManagedIdentityIdParameter is null)
        {
            return;
        }

        // REVIEW: This is is a little hacky, we should probably have a better way to do this
        var id = BicepFunction.Interpolate($"{_containerRegistryManagedIdentityIdParameter}").Compile().ToString();

        identity.ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned;
        identity.UserAssignedIdentities[id] = new UserAssignedIdentityDetails();
    }

    protected void AddContainerRegistryParameters(Action<BicepList<ContainerAppRegistryCredentials>> setRegistries)
    {
        if (_containerRegistryUrlParameter is null || _containerRegistryManagedIdentityIdParameter is null)
        {
            return;
        }

        setRegistries([
            new ContainerAppRegistryCredentials
            {
                Server = _containerRegistryUrlParameter,
                Identity = _containerRegistryManagedIdentityIdParameter
            }
        ]);
    }

#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
    protected void AddProbes(ContainerAppContainer containerAppContainer)
    {
        if (!Resource.TryGetAnnotationsOfType<ProbeAnnotation>(out var probeAnnotations))
        {
            return;
        }

        foreach (var probeAnnotation in probeAnnotations)
        {
            ContainerAppProbe? containerAppProbe = null;
            if (probeAnnotation is EndpointProbeAnnotation endpointProbeAnnotation
                && _endpointMapping.TryGetValue(endpointProbeAnnotation.EndpointReference.EndpointName, out var endpointMapping))
            {
                // In ACA probes work on internal network only and don't go through ingress so if we have a
                // probe associated to an endpoint used for ingress we force scheme to "http".
                // Port is always the target port of the container.
                var scheme = endpointMapping.Scheme;
                if (endpointMapping.IsHttpIngress)
                {
                    scheme = "http";
                }

                containerAppProbe = new ContainerAppProbe()
                {
                    HttpGet = new()
                    {
                        Path = endpointProbeAnnotation.Path,
                        Port = AsInt(GetEndpointValue(endpointMapping, EndpointProperty.TargetPort)),
                        Scheme = scheme is "https" ? ContainerAppHttpScheme.Https : ContainerAppHttpScheme.Http,
                    },
                };
            }

            if (containerAppProbe is not null)
            {
                containerAppProbe.ProbeType = probeAnnotation.Type switch
                {
                    ProbeType.Startup => ContainerAppProbeType.Startup,
                    ProbeType.Readiness => ContainerAppProbeType.Readiness,
                    _ => ContainerAppProbeType.Liveness,
                };
                containerAppProbe.InitialDelaySeconds = probeAnnotation.InitialDelaySeconds;
                containerAppProbe.PeriodSeconds = probeAnnotation.PeriodSeconds;
                containerAppProbe.TimeoutSeconds = probeAnnotation.TimeoutSeconds;
                containerAppProbe.SuccessThreshold = probeAnnotation.SuccessThreshold;
                containerAppProbe.FailureThreshold = probeAnnotation.FailureThreshold;

                containerAppContainer.Probes.Add(new BicepValue<ContainerAppProbe>(containerAppProbe));
            }
        }
    }
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

    protected enum SecretType
    {
        None,
        Normal,
        KeyVault,
    }
}
