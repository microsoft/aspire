// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Utils;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.AppService;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting.Azure.AppService;

internal sealed class AzureAppServiceWebsiteContext(
    IResource resource,
    AzureAppServiceEnvironmentContext environmentContext)
{
    public IResource Resource => resource;

    record struct EndpointMapping(string Scheme, BicepValue<string> Host, int Port, int? TargetPort, bool IsHttpIngress, bool External);

    private readonly Dictionary<string, EndpointMapping> _endpointMapping = [];
    private readonly Dictionary<string, EndpointMapping> _slotEndpointMapping = [];

    // Resolved environment variables and command line args
    // These contain the values that need to be further transformed into
    // bicep compatible values
    public Dictionary<string, object> EnvironmentVariables { get; } = [];
    public List<object> Args { get; } = [];

    private AzureResourceInfrastructure? _infrastructure;
    public AzureResourceInfrastructure Infra => _infrastructure ?? throw new InvalidOperationException("Infra is not set");

    // Naming the app service is globally unique (domain names), so we use the resource group ID to create a unique name
    // within the naming spec for the app service.
    private BicepValue<string> HostName => BicepFunction.Take(
        BicepFunction.Interpolate($"{BicepFunction.ToLower(resource.Name)}-{AzureAppServiceEnvironmentResource.GetWebSiteSuffixBicep()}"), 60);

    /// <summary>
    /// Gets the hostname for a deployment slot by appending the slot name to the base website name.
    /// </summary>
    /// <param name="deploymentSlot">The deployment slot name.</param>
    /// <returns>A <see cref="BicepValue{T}"/> representing the slot hostname, truncated to 60 characters.</returns>
    public BicepValue<string> GetSlotHostName(BicepValue<string> deploymentSlot)
    {
        return BicepFunction.Take(
        BicepFunction.Interpolate($"{BicepFunction.ToLower(resource.Name)}-{AzureAppServiceEnvironmentResource.GetWebSiteSuffixBicep()}-{BicepFunction.ToLower(deploymentSlot)}"), 60);
    }

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        ProcessEndpoints();
        await ProcessEnvironmentAsync(cancellationToken).ConfigureAwait(true);
        await ProcessArgumentsAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task ProcessEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var environmentCallbacks))
        {
            var context = new EnvironmentCallbackContext(
                environmentContext.ExecutionContext, resource, EnvironmentVariables, cancellationToken);

            foreach (var c in environmentCallbacks)
            {
                await c.Callback(context).ConfigureAwait(true);
            }
        }
    }

    private async Task ProcessArgumentsAsync(CancellationToken cancellationToken)
    {
        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var commandLineArgsCallbackAnnotations))
        {
            var context = new CommandLineArgsCallbackContext(Args, resource, cancellationToken)
            {
                ExecutionContext = environmentContext.ExecutionContext
            };

            foreach (var c in commandLineArgsCallbackAnnotations)
            {
                await c.Callback(context).ConfigureAwait(true);
            }
        }
    }

    private void ProcessEndpoints()
    {
        // Resolve endpoint ports using the centralized helper
        var resolvedEndpoints = resource.ResolveEndpoints();

        if (resolvedEndpoints.Count == 0)
        {
            return;
        }

        // Only http/https are supported in App Service
        var unsupportedEndpoints = resolvedEndpoints.Where(r => r.Endpoint.UriScheme is not ("http" or "https")).ToArray();
        if (unsupportedEndpoints.Length > 0)
        {
            throw new NotSupportedException($"The endpoint(s) {string.Join(", ", unsupportedEndpoints.Select(r => $"'{r.Endpoint.Name}'"))} on resource '{resource.Name}' specifies an unsupported scheme. Only http and https are supported in App Service.");
        }

        // App Service supports only one target port
        var targetPortEndpoints = resolvedEndpoints
            .Where(r => r.Endpoint.IsExternal)
            .Select(r => r.TargetPort.Value)
            .Distinct()
            .ToList();

        if (targetPortEndpoints.Count > 1)
        {
            throw new NotSupportedException("App Service does not support resources with multiple external endpoints.");
        }

        foreach (var resolved in resolvedEndpoints)
        {
            var endpoint = resolved.Endpoint;

            if (!endpoint.IsExternal)
            {
                throw new NotSupportedException($"The endpoint '{endpoint.Name}' on resource '{resource.Name}' is not external. App Service only supports external endpoints.");
            }

            // For App Service, we ignore port mappings since ports are handled by the platform
            // TargetPort is null only for default ProjectResource endpoints (container port decides)
            _endpointMapping[endpoint.Name] = new(
                Scheme: endpoint.UriScheme,
                Host: HostName,
                Port: endpoint.UriScheme == "https" ? 443 : 80,
                TargetPort: resolved.TargetPort,
                IsHttpIngress: true,
                External: true); // All App Service endpoints are external
        }
    }

    private (object, SecretType) ProcessValue(object value, SecretType secretType = SecretType.None, object? parent = null, bool isSlot = false)
    {
        if (value is string s)
        {
            return (s, secretType);
        }

        if (value is EndpointReference ep)
        {
            var context = environmentContext.GetAppServiceContext(ep.Resource);
            return isSlot ?
                (GetEndpointValue(context._slotEndpointMapping[ep.EndpointName], EndpointProperty.Url), secretType) :
                (GetEndpointValue(context._endpointMapping[ep.EndpointName], EndpointProperty.Url), secretType);
        }

        if (value is ParameterResource param)
        {
            var st = param.Secret ? SecretType.Normal : secretType;
            return (AllocateParameter(param, secretType: st), st);
        }

        if (value is ConnectionStringReference cs)
        {
            return ProcessValue(cs.Resource.ConnectionStringExpression, secretType, parent, isSlot);
        }

        if (value is IResourceWithConnectionString csrs)
        {
            return ProcessValue(csrs.ConnectionStringExpression, secretType, parent, isSlot);
        }

        if (value is BicepOutputReference output)
        {
            return (AllocateParameter(output, secretType: secretType), secretType);
        }

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
            var context = environmentContext.GetAppServiceContext(epExpr.Endpoint.Resource);
            var mapping = isSlot ? context._slotEndpointMapping[epExpr.Endpoint.EndpointName] : context._endpointMapping[epExpr.Endpoint.EndpointName];
            var val = GetEndpointValue(mapping, epExpr.Property);
            return (val, secretType);
        }

        if (value is ReferenceExpression expr)
        {
            if (expr.Format == "{0}" && expr.ValueProviders.Count == 1)
            {
                var val = ProcessValue(expr.ValueProviders[0], secretType, parent: parent, isSlot);

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
                var (val, secret) = ProcessValue(vp, secretType, expr, isSlot);
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

        throw new NotSupportedException($"Unsupported value type {value.GetType()}");
    }

    private static BicepValue<string> ResolveValue(object val)
    {
        return val switch
        {
            BicepValue<string> s => s,
            string s => s,
            ProvisioningParameter p => p,
            FormattableString fs => BicepFunction.Interpolate(fs),
            _ => throw new NotSupportedException($"Unsupported value type {val.GetType()}")
        };
    }

    public void BuildWebSite(AzureResourceInfrastructure infra)
    {
        bool buildWebAppAndSlot = resource.TryGetAnnotationsOfType<AzureAppServiceWebsiteRefreshProvisionableResourceAnnotation>(out _);

        _infrastructure = infra;

        // Check for deployment slot
        // If specified, update hostnames to endpoint references
        BicepValue<string>? deploymentSlotValue = null;
        if (environmentContext.Environment.DeploymentSlotParameter is not null || environmentContext.Environment.DeploymentSlot is not null)
        {
            deploymentSlotValue = environmentContext.Environment.DeploymentSlotParameter != null
                ? environmentContext.Environment.DeploymentSlotParameter.AsProvisioningParameter(infra)
                : environmentContext.Environment.DeploymentSlot!;

            UpdateHostNameForSlot(deploymentSlotValue);
        }

        if (deploymentSlotValue is not null && buildWebAppAndSlot)
        {
            BuildWebSiteAndSlot(infra, deploymentSlotValue!);
            return;
        }

        BuildWebSiteCore(infra, deploymentSlotValue);
    }

    /// <summary>
    /// Creates and configures an Azure App Service WebSite or WebSiteSlot.
    /// </summary>
    /// <param name="infra">The Azure resource infrastructure.</param>
    /// <param name="name">The name of the website or slot.</param>
    /// <param name="appServicePlanParameter">The App Service plan parameter.</param>
    /// <param name="acrMidParameter">The Azure Container Registry managed identity parameter.</param>
    /// <param name="acrClientIdParameter">The Azure Container Registry client ID parameter.</param>
    /// <param name="containerImage">The container image parameter.</param>
    /// <param name="isSlot">Indicates whether this is a deployment slot.</param>
    /// <param name="parentWebSite">The parent website when creating a slot.</param>
    /// <param name="deploymentSlot">The deployment slot name.</param>
    /// <returns>A dynamic object representing either a WebSite or WebSiteSlot.</returns>
    private object CreateAndConfigureWebSite(
    AzureResourceInfrastructure infra,
    BicepValue<string> name,
    BicepValue<ResourceIdentifier> appServicePlanParameter,
    BicepValue<string> acrMidParameter,
    ProvisioningParameter acrClientIdParameter,
    ProvisioningParameter containerImage,
    bool isSlot = false,
    WebSite? parentWebSite = null,
    BicepValue<string>? deploymentSlot = null)
    {
        object webSite;
        object mainContainer;

        if (isSlot && parentWebSite is not null && deploymentSlot is not null)
        {
            var slot = new WebSiteSlot("webappslot")
            {
                Parent = parentWebSite,
                Name = deploymentSlot,
                AppServicePlanId = appServicePlanParameter,
                SiteConfig = new SiteConfigProperties()
                {
                    LinuxFxVersion = "SITECONTAINERS",
                    AcrUserManagedIdentityId = acrClientIdParameter,
                    UseManagedIdentityCreds = true,
                    NumberOfWorkers = 30,
                    AppSettings = []
                },
                Identity = new ManagedServiceIdentity()
                {
                    ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned,
                    UserAssignedIdentities = []
                },
            };

            var slotContainer = new SiteSlotSiteContainer("mainContainerSlot")
            {
                Parent = slot,
                Name = "main",
                Image = containerImage,
                AuthType = SiteContainerAuthType.UserAssigned,
                UserManagedIdentityClientId = acrClientIdParameter,
                IsMain = true
            };

            webSite = slot;
            mainContainer = slotContainer;
        }
        else
        {
            var site = new WebSite("webapp")
            {
                Name = name,
                AppServicePlanId = appServicePlanParameter,
                // Creating the app service with new sidecar configuration
                SiteConfig = new SiteConfigProperties()
                {
                    LinuxFxVersion = "SITECONTAINERS",
                    AcrUserManagedIdentityId = acrClientIdParameter,
                    UseManagedIdentityCreds = true,
                    // Setting NumberOfWorkers to maximum allowed value for Premium SKU
                    // https://learn.microsoft.com/en-us/azure/app-service/manage-scale-up
                    // This is required due to use of feature PerSiteScaling for the App Service plan
                    // We want the web apps to scale normally as defined for the app service plan
                    // so setting the maximum number of workers to the maximum allowed for Premium V2 SKU.
                    NumberOfWorkers = 30,
                    AppSettings = []
                },
                Identity = new ManagedServiceIdentity()
                {
                    ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned,
                    UserAssignedIdentities = []
                },
            };

            // Defining the main container for the app service
            var siteContainer = new SiteContainer("mainContainer")
            {
                Parent = site,
                Name = "main",
                Image = containerImage,
                AuthType = SiteContainerAuthType.UserAssigned,
                UserManagedIdentityClientId = acrClientIdParameter,
                IsMain = true
            };

            webSite = site;
            mainContainer = siteContainer;
        }

        // There should be a single valid target port
        if (_endpointMapping.FirstOrDefault() is var (_, mapping))
        {
            var targetPort = GetEndpointValue(mapping, EndpointProperty.TargetPort);

            if (mainContainer is SiteContainer container)
            {
                container.TargetPort = targetPort;
            }
            else if (mainContainer is SiteSlotSiteContainer slotContainer)
            {
                slotContainer.TargetPort = targetPort;
            }

            if (webSite is WebSite site)
            {
                site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "WEBSITES_PORT", Value = targetPort });
            }
            else if (webSite is WebSiteSlot slot)
            {
                slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "WEBSITES_PORT", Value = targetPort });
            }
        }

        foreach (var kv in EnvironmentVariables)
        {
            var (val, secretType) = ProcessValue(kv.Value, isSlot: isSlot);
            var value = ResolveValue(val);

            if (secretType == SecretType.KeyVault)
            {
                // https://learn.microsoft.com/azure/app-service/app-service-key-vault-references?tabs=azure-cli#-understand-source-app-settings-from-key-vault
                // @Microsoft.KeyVault({referenceString})
                value = BicepFunction.Interpolate($"@Microsoft.KeyVault(SecretUri={val})");
            }

            if (webSite is WebSite site)
            {
                site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = kv.Key, Value = value });
            }
            else if (webSite is WebSiteSlot slot)
            {
                slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = kv.Key, Value = value });
            }
        }

        if (Args.Count > 0)
        {
            var args = new List<BicepValue<string>>();

            foreach (var arg in Args)
            {
                var (val, secretType) = ProcessValue(arg);
                var value = ResolveValue(val);

                args.Add(value);
            }

            // App Service does not support array arguments, so we need to join them into a single string
            static FunctionCallExpression Join(BicepExpression args, string delimeter) =>
                new(new IdentifierExpression("join"), args, new StringLiteralExpression(delimeter));

            var arrayExpression = new ArrayExpression([.. args.Select(a => a.Compile())]);

            if (mainContainer is SiteContainer container)
            {
                container.StartUpCommand = Join(arrayExpression, " ");
            }
            else if (mainContainer is SiteSlotSiteContainer slotContainer)
            {
                slotContainer.StartUpCommand = Join(arrayExpression, " ");
            }
        }

        if (mainContainer is SiteContainer mainSiteContainer)
        {
            infra.Add(mainSiteContainer);
        }
        else if (mainContainer is SiteSlotSiteContainer mainSiteSlotContainer)
        {
            infra.Add(mainSiteSlotContainer);
        }

        var id = BicepFunction.Interpolate($"{acrMidParameter}").Compile().ToString();

        if (webSite is WebSite siteObject)
        {
            siteObject.Identity.UserAssignedIdentities[id] = new UserAssignedIdentityDetails();
        }
        else if (webSite is WebSiteSlot slot)
        {
            slot.Identity.UserAssignedIdentities[id] = new UserAssignedIdentityDetails();
        }

        // This is the user assigned identity associated with the web app, not the container registry
        if (resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentityAnnotation))
        {
            var appIdentityResource = appIdentityAnnotation.IdentityResource;

            var computeIdentity = appIdentityResource.Id.AsProvisioningParameter(infra);

            var cid = BicepFunction.Interpolate($"{computeIdentity}").Compile().ToString();

            if (webSite is WebSite site)
            {
                site.KeyVaultReferenceIdentity = computeIdentity;
                site.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned;
                site.Identity.UserAssignedIdentities[cid] = new UserAssignedIdentityDetails();

                site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
                {
                    Name = "AZURE_CLIENT_ID",
                    Value = appIdentityResource.ClientId.AsProvisioningParameter(infra)
                });

                // DefaultAzureCredential should only use ManagedIdentityCredential when running in Azure
                site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
                {
                    Name = "AZURE_TOKEN_CREDENTIALS",
                    Value = "ManagedIdentityCredential"
                });
            }
            else if (webSite is WebSiteSlot slot)
            {
                slot.KeyVaultReferenceIdentity = computeIdentity;
                slot.Identity.UserAssignedIdentities[id] = new UserAssignedIdentityDetails();
                slot.Identity.ManagedServiceIdentityType = ManagedServiceIdentityType.UserAssigned;
                slot.Identity.UserAssignedIdentities[cid] = new UserAssignedIdentityDetails();
                slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
                {
                    Name = "AZURE_CLIENT_ID",
                    Value = appIdentityResource.ClientId.AsProvisioningParameter(infra)
                });
                // DefaultAzureCredential should only use ManagedIdentityCredential when running in Azure
                slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
                {
                    Name = "AZURE_TOKEN_CREDENTIALS",
                    Value = "ManagedIdentityCredential"
                });
            }
        }

        // Added appsetting to identify the resource in a specific aspire environment
        if (webSite is WebSite siteObj)
        {
            siteObj.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "ASPIRE_ENVIRONMENT_NAME", Value = environmentContext.Environment.Name });
        }
        else if (webSite is WebSiteSlot slot)
        {
            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "ASPIRE_ENVIRONMENT_NAME", Value = environmentContext.Environment.Name });
        }

        // Probes
#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        if (resource.TryGetAnnotationsOfType<ProbeAnnotation>(out var probeAnnotations))
        {
            // AppService allow only one "health check" with only path, so prioritize "liveness" and/or take the first one
            var endpointProbeAnnotation = probeAnnotations
                .OfType<EndpointProbeAnnotation>()
                .OrderBy(probeAnnotation => probeAnnotation.Type == ProbeType.Liveness ? 0 : 1)
                .FirstOrDefault();

            if (endpointProbeAnnotation is not null)
            {
                if (webSite is WebSiteSlot slot)
                {
                    slot.SiteConfig.HealthCheckPath = endpointProbeAnnotation.Path;
                }
                else if (webSite is WebSite site)
                {
                    site.SiteConfig.HealthCheckPath = endpointProbeAnnotation.Path;
                }
            }
        }
#pragma warning restore ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        RoleAssignment? webSiteRa = null;
        if (environmentContext.Environment.EnableDashboard)
        {
            webSiteRa = AddDashboardPermissionAndSettings(webSite, acrClientIdParameter, deploymentSlot);
        }

        if (webSite is WebSite webSiteObject)
        {
            infra.Add(webSiteObject);
        }
        else if (webSite is WebSiteSlot slot)
        {
            infra.Add(slot);
        }

        if (webSiteRa is not null)
        {
            infra.Add(webSiteRa);
        }

        if (environmentContext.Environment.EnableApplicationInsights)
        {
            EnableApplicationInsightsForWebSite(webSite);
        }

        return webSite;
    }

    /// <summary>
    /// Builds either a standalone website or a deployment slot based on whether a deployment slot is specified.
    /// </summary>
    /// <param name="infra">The Azure resource infrastructure.</param>
    /// <param name="deploymentSlot">The optional deployment slot name.</param>
    private void BuildWebSiteCore(
        AzureResourceInfrastructure infra,
        BicepValue<string>? deploymentSlot = null)
    {
        _infrastructure = infra;

        _ = environmentContext.Environment.ContainerRegistryUrl.AsProvisioningParameter(infra);
        var appServicePlanParameter = environmentContext.Environment.PlanIdOutputReference.AsProvisioningParameter(infra);
        var acrMidParameter = environmentContext.Environment.ContainerRegistryManagedIdentityId.AsProvisioningParameter(infra);
        var acrClientIdParameter = environmentContext.Environment.ContainerRegistryClientId.AsProvisioningParameter(infra);
        var containerImage = AllocateParameter(new ContainerImageReference(Resource));

        // Create parent WebSite from existing
        WebSite? parentWebSite = null;

        if (deploymentSlot is not null)
        {
            parentWebSite = WebSite.FromExisting("webapp");
            parentWebSite.Name = HostName;
            Infra.Add(parentWebSite);
        }

        var webSite = CreateAndConfigureWebSite(
            infra,
            HostName,
            appServicePlanParameter,
            acrMidParameter,
            acrClientIdParameter,
            containerImage,
            isSlot: deploymentSlot is not null,
            parentWebSite: parentWebSite,
            deploymentSlot: deploymentSlot);

        // Allow users to customize the web app here
        if (deploymentSlot is not null)
        {
            if (resource.TryGetAnnotationsOfType<AzureAppServiceWebsiteSlotCustomizationAnnotation>(out var customizeWebSiteSlotAnnotations))
            {
                var slot = (WebSiteSlot)webSite;
                foreach (var customizeWebSiteSlotAnnotation in customizeWebSiteSlotAnnotations)
                {
                    customizeWebSiteSlotAnnotation.Configure(Infra, slot);
                }
            }
        }
        else
        {
            if (resource.TryGetAnnotationsOfType<AzureAppServiceWebsiteCustomizationAnnotation>(out var customizeWebSiteAnnotations))
            {
                var site = (WebSite)webSite;
                foreach (var customizeWebSiteAnnotation in customizeWebSiteAnnotations)
                {
                    customizeWebSiteAnnotation.Configure(infra, site);
                }
            }
        }
    }

    /// <summary>
    /// Builds both the main website and a deployment slot when deploying to a slot for the first time.
    /// </summary>
    /// <param name="infra">The Azure resource infrastructure.</param>
    /// <param name="deploymentSlot">The deployment slot name.</param>
    private void BuildWebSiteAndSlot(
        AzureResourceInfrastructure infra,
        BicepValue<string> deploymentSlot)
    {
        _infrastructure = infra;

        _ = environmentContext.Environment.ContainerRegistryUrl.AsProvisioningParameter(infra);
        var appServicePlanParameter = environmentContext.Environment.PlanIdOutputReference.AsProvisioningParameter(infra);
        var acrMidParameter = environmentContext.Environment.ContainerRegistryManagedIdentityId.AsProvisioningParameter(infra);
        var acrClientIdParameter = environmentContext.Environment.ContainerRegistryClientId.AsProvisioningParameter(infra);
        var containerImage = AllocateParameter(new ContainerImageReference(Resource));

        // Main site
        var webSite = (WebSite)CreateAndConfigureWebSite(
            infra,
            HostName,
            appServicePlanParameter,
            acrMidParameter,
            acrClientIdParameter,
            containerImage,
            isSlot: false);

        // Slot
        var webSiteSlot = (WebSiteSlot)CreateAndConfigureWebSite(
            infra,
            deploymentSlot,
            appServicePlanParameter,
            acrMidParameter,
            acrClientIdParameter,
            containerImage,
            isSlot: true,
            parentWebSite: (WebSite)webSite,
            deploymentSlot: deploymentSlot);

        // Allow users to customize the website
        if (resource.TryGetAnnotationsOfType<AzureAppServiceWebsiteCustomizationAnnotation>(out var customizeWebSiteAnnotations))
        {
            foreach (var customizeWebSiteAnnotation in customizeWebSiteAnnotations)
            {
                customizeWebSiteAnnotation.Configure(infra, webSite);
            }
        }

        // Allow users to customize the slot
        if (resource.TryGetAnnotationsOfType<AzureAppServiceWebsiteSlotCustomizationAnnotation>(out var customizeWebSiteSlotAnnotations))
        {
            foreach (var customizeWebSiteSlotAnnotation in customizeWebSiteSlotAnnotations)
            {
                customizeWebSiteSlotAnnotation.Configure(infra, webSiteSlot);
            }
        }
    }

    private BicepValue<string> GetEndpointValue(EndpointMapping mapping, EndpointProperty property)
    {
        return property switch
        {
            EndpointProperty.Url => BicepFunction.Interpolate($"{mapping.Scheme}://{mapping.Host}.azurewebsites.net"),
            EndpointProperty.Host => BicepFunction.Interpolate($"{mapping.Host}.azurewebsites.net"),
            EndpointProperty.Port => mapping.Port.ToString(CultureInfo.InvariantCulture),
            EndpointProperty.TargetPort => mapping.TargetPort?.ToString(CultureInfo.InvariantCulture) ?? (BicepValue<string>)AllocateParameter(new ContainerPortReference(Resource)),
            EndpointProperty.Scheme => mapping.Scheme,
            EndpointProperty.HostAndPort => BicepFunction.Interpolate($"{mapping.Host}.azurewebsites.net"),
            EndpointProperty.IPV4Host => BicepFunction.Interpolate($"{mapping.Host}.azurewebsites.net"),
            _ => throw new NotSupportedException($"Unsupported endpoint property {property}")
        };
    }

    private BicepValue<string> AllocateKeyVaultSecretUriReference(IAzureKeyVaultSecretReference secretReference)
    {
        var secret = secretReference.AsKeyVaultSecret(Infra);

        // https://learn.microsoft.com/azure/app-service/app-service-key-vault-references?tabs=azure-cli#-understand-source-app-settings-from-key-vault
        return secret.Properties.SecretUri;
    }

    private ProvisioningParameter AllocateParameter(IManifestExpressionProvider parameter, SecretType secretType = SecretType.None)
    {
        return parameter.AsProvisioningParameter(Infra, isSecure: secretType == SecretType.Normal);
    }

    private RoleAssignment AddDashboardPermissionAndSettings(object webSite, ProvisioningParameter acrClientIdParameter, BicepValue<string>? deploymentSlot = null)
    {
        bool isSlot = deploymentSlot is not null;
        var dashboardUri = environmentContext.Environment.DashboardUriReference.AsProvisioningParameter(Infra);
        var contributorId = environmentContext.Environment.WebsiteContributorManagedIdentityId.AsProvisioningParameter(Infra);
        var contributorPrincipalId = environmentContext.Environment.WebsiteContributorManagedIdentityPrincipalId.AsProvisioningParameter(Infra);
        var otelServiceName = isSlot ? BicepFunction.Interpolate($"{resource.Name}-{deploymentSlot}") : resource.Name;

        // Add Website Contributor role assignment to dashboard's managed identity for this webapp
        var websiteRaId = BicepFunction.GetSubscriptionResourceId(
                    "Microsoft.Authorization/roleDefinitions",
                    "de139f84-1756-47ae-9be6-808fbbe84772");

        string raResourceName = isSlot
            ? Infrastructure.NormalizeBicepIdentifier($"{Infra.AspireResource.Name}_slot_ra")
            : Infrastructure.NormalizeBicepIdentifier($"{Infra.AspireResource.Name}_ra");

        RoleAssignment? roleAssignment = null;

        if (webSite is WebSite site)
        {
            // Add the appsettings specific to sending telemetry data to dashboard
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_SERVICE_NAME", Value = otelServiceName });
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_EXPORTER_OTLP_PROTOCOL", Value = "grpc" });
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_EXPORTER_OTLP_ENDPOINT", Value = "http://localhost:6001" });
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "WEBSITE_ENABLE_ASPIRE_OTEL_SIDECAR", Value = "true" });
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_COLLECTOR_URL", Value = dashboardUri });
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_CLIENT_ID", Value = acrClientIdParameter });

            var websiteRaName = BicepFunction.CreateGuid(site.Id, contributorId, websiteRaId);
            roleAssignment = new RoleAssignment(raResourceName)
            {
                Name = websiteRaName,
                Scope = new IdentifierExpression(site.BicepIdentifier),
                PrincipalType = RoleManagementPrincipalType.ServicePrincipal,
                PrincipalId = contributorPrincipalId,
                RoleDefinitionId = websiteRaId,
            };
        }
        else if (webSite is WebSiteSlot slot)
        {
            // Add the appsettings specific to sending telemetry data to dashboard
            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_SERVICE_NAME", Value = otelServiceName });
            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_EXPORTER_OTLP_PROTOCOL", Value = "grpc" });
            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_EXPORTER_OTLP_ENDPOINT", Value = "http://localhost:6001" });
            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "WEBSITE_ENABLE_ASPIRE_OTEL_SIDECAR", Value = "true" });
            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_COLLECTOR_URL", Value = dashboardUri });
            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair { Name = "OTEL_CLIENT_ID", Value = acrClientIdParameter });

            var websiteRaName = BicepFunction.CreateGuid(slot.Id, contributorId, websiteRaId);
            roleAssignment = new RoleAssignment(raResourceName)
            {
                Name = websiteRaName,
                Scope = new IdentifierExpression(slot.BicepIdentifier),
                PrincipalType = RoleManagementPrincipalType.ServicePrincipal,
                PrincipalId = contributorPrincipalId,
                RoleDefinitionId = websiteRaId,
            };
        }

        return roleAssignment!;
    }

    private void EnableApplicationInsightsForWebSite(object webSite)
    {
        var appInsightsInstrumentationKey = environmentContext.Environment.AzureAppInsightsInstrumentationKeyReference.AsProvisioningParameter(Infra);
        var appInsightsConnectionString = environmentContext.Environment.AzureAppInsightsConnectionStringReference.AsProvisioningParameter(Infra);

        if (webSite is WebSite site)
        {
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
            {
                Name = "APPINSIGHTS_INSTRUMENTATIONKEY",
                Value = appInsightsInstrumentationKey
            });
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
            {
                Name = "APPLICATIONINSIGHTS_CONNECTION_STRING",
                Value = appInsightsConnectionString
            });
            site.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
            {
                Name = "ApplicationInsightsAgent_EXTENSION_VERSION",
                Value = "~3"
            });
        }
        else if (webSite is WebSiteSlot slot)
        {
            // Website configuration for Application Insights
            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
            {
                Name = "APPINSIGHTS_INSTRUMENTATIONKEY",
                Value = appInsightsInstrumentationKey
            });

            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
            {
                Name = "APPLICATIONINSIGHTS_CONNECTION_STRING",
                Value = appInsightsConnectionString
            });

            slot.SiteConfig.AppSettings.Add(new AppServiceNameValuePair
            {
                Name = "ApplicationInsightsAgent_EXTENSION_VERSION",
                Value = "~3"
            });
        }                                                  
    }

    /// <summary>
    /// Updates endpoint mappings to use deployment slot hostnames.
    /// </summary>
    /// <param name="slotName">The deployment slot name.</param>
    private void UpdateHostNameForSlot(BicepValue<string> slotName)
    {
        foreach (var (name, mapping) in _endpointMapping.ToList())
        {
            BicepValue<string> hostValue;

            hostValue = GetSlotHostName(slotName);
            _slotEndpointMapping[name] = mapping with { Host = hostValue };
        }
    }

    enum SecretType
    {
        None,
        Normal,
        KeyVault
    }
}
