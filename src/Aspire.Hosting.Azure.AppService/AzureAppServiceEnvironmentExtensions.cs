// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.AppService;
using Aspire.Hosting.Pipelines;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.ApplicationInsights;
using Azure.Provisioning.AppService;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.OperationalInsights;
using Azure.Provisioning.Roles;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extensions for adding Azure App Service Environment resources to a distributed application builder.
/// </summary>
public static partial class AzureAppServiceEnvironmentExtensions
{
    internal static IDistributedApplicationBuilder AddAzureAppServiceInfrastructureCore(this IDistributedApplicationBuilder builder)
    {
        builder.AddAzureProvisioning();

        builder.Services.Configure<AzureProvisioningOptions>(options => options.SupportsTargetedRoleAssignments = true);

        // Register the pipeline step idempotently. AddAzureAppServiceInfrastructureCore can be
        // called more than once (e.g. when AddAzureAppServiceEnvironment is called for multiple
        // environments). The marker singleton ensures we only add the step the first time.
        //
        // The per-environment work (creating App Service resources and DeploymentTargetAnnotations)
        // is registered as a separate per-environment pipeline step on AzureAppServiceEnvironmentResource.
        // This global step only validates that no resource has a PublishAs* annotation when there are
        // no AzureAppServiceEnvironmentResource instances in the model.
        if (builder.Services.All(d => d.ServiceType != typeof(AppServicePipelineStepMarker)))
        {
            builder.Services.AddSingleton<AppServicePipelineStepMarker>();

            builder.Pipeline.AddStep(
                name: AppServicePipelineStepMarker.StepName,
                action: ctx =>
                {
                    if (!ctx.ExecutionContext.IsPublishMode)
                    {
                        return Task.CompletedTask;
                    }

                    if (!ctx.Model.Resources.OfType<AzureAppServiceEnvironmentResource>().Any())
                    {
                        foreach (var r in ctx.Model.GetComputeResources())
                        {
                            if (r.HasAnnotationOfType<AzureAppServiceWebsiteCustomizationAnnotation>())
                            {
                                throw new InvalidOperationException($"Resource '{r.Name}' is configured to publish as an Azure AppService Website, but there are no '{nameof(AzureAppServiceEnvironmentResource)}' resources. Ensure you have added one by calling '{nameof(AddAzureAppServiceEnvironment)}'.");
                            }
                        }
                    }

                    return Task.CompletedTask;
                },
                requiredBy: WellKnownPipelineSteps.BeforeStart);
        }

        return builder;
    }

    private sealed class AppServicePipelineStepMarker
    {
        public const string StepName = "validate-azure-app-service";
    }

    /// <summary>
    /// Adds a azure app service environment resource to the distributed application builder.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> AddAzureAppServiceEnvironment(this IDistributedApplicationBuilder builder, string name)
    {
        builder.AddAzureAppServiceInfrastructureCore();

        // Create the default container registry resource before creating the environment
        var registryName = $"{name}-acr";
        var defaultRegistry = CreateDefaultAzureContainerRegistry(builder, registryName);

        var resource = new AzureAppServiceEnvironmentResource(name, static infra =>
        {
            var resource = (AzureAppServiceEnvironmentResource)infra.AspireResource;
            var prefix = Infrastructure.NormalizeBicepIdentifier(resource.Name);

            // This tells azd to avoid creating infrastructure
            var userPrincipalId = new ProvisioningParameter(AzureBicepResource.KnownParameters.UserPrincipalId, typeof(string)) { Value = new BicepValue<string>(string.Empty) };
            infra.Add(userPrincipalId);

            var tags = new ProvisioningParameter("tags", typeof(object))
            {
                Value = new BicepDictionary<string>()
            };

            infra.Add(tags);

            UserAssignedIdentity? newIdentity = null;
            BicepValue<string> managedIdentityIdOutputValue;
            BicepValue<string> managedIdentityClientIdOutputValue;

            if (resource.TryGetLastAnnotation<AzureAppServiceEnvironmentAcrPullIdentityAnnotation>(out var identityAnnotation))
            {
                // The user has supplied an existing identity (commonly via AddAzureUserAssignedIdentity +
                // .WithRoleAssignments(acr, AcrPull)). Skip creating env_mi + the AcrPull role assignment
                // here and have the env module read the identity id/client id from parameters wired to the
                // identity module's outputs.
                managedIdentityIdOutputValue = identityAnnotation.Identity.Id.AsProvisioningParameter(infra);
                managedIdentityClientIdOutputValue = identityAnnotation.Identity.ClientId.AsProvisioningParameter(infra);
            }
            else
            {
                newIdentity = new UserAssignedIdentity($"{prefix}_mi")
                {
                    Tags = tags
                };

                infra.Add(newIdentity);
                managedIdentityIdOutputValue = newIdentity.Id.ToBicepExpression();
                managedIdentityClientIdOutputValue = newIdentity.ClientId.ToBicepExpression();
            }

            AzureProvisioningResource? registry = null;
            if (resource.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var registryReferenceAnnotation) &&
                registryReferenceAnnotation.Registry is AzureProvisioningResource explicitRegistry)
            {
                registry = explicitRegistry;
            }
            else if (resource.DefaultContainerRegistry is not null)
            {
                registry = resource.DefaultContainerRegistry;
            }

            if (registry is null)
            {
                throw new InvalidOperationException($"No container registry associated with environment '{resource.Name}'. This should have been added automatically.");
            }

            var containerRegistry = (ContainerRegistryService)registry.AddAsExistingResource(infra);
            infra.Add(containerRegistry);

            if (newIdentity is not null)
            {
                var pullRa = containerRegistry.CreateRoleAssignment(ContainerRegistryBuiltInRole.AcrPull, newIdentity);

                // There's a bug in the CDK, see https://github.com/Azure/azure-sdk-for-net/issues/47265
                pullRa.Name = BicepFunction.CreateGuid(containerRegistry.Id, newIdentity.Id, pullRa.RoleDefinitionId);
                infra.Add(pullRa);
            }

            AppServicePlan plan;
            if (resource.IsExisting())
            {
                // The Aspire resource models the App Service Plan. When users mark it existing,
                // keep provisioning the supporting app resources but reference the supplied plan
                // instead of declaring a new Microsoft.Web/serverfarms resource.
                plan = (AppServicePlan)resource.AddAsExistingResource(infra);
            }
            else
            {
                plan = new AppServicePlan($"{prefix}_asplan")
                {
                    Sku = new AppServiceSkuDescription
                    {
                        Name = "P0V3",
                        Tier = "Premium"
                    },
                    Kind = "Linux",
                    IsReserved = true,
                    // Enable perSiteScaling so each app service can scale independently
                    IsPerSiteScaling = true
                };

                infra.Add(plan);
            }

            infra.Add(new ProvisioningOutput("name", typeof(string))
            {
                Value = plan.Name.ToBicepExpression()
            });

            infra.Add(new ProvisioningOutput("planId", typeof(string))
            {
                Value = plan.Id.ToBicepExpression()
            });

            infra.Add(new ProvisioningOutput("webSiteSuffix", typeof(string))
            {
                Value = AzureAppServiceEnvironmentResource.GetWebSiteSuffixBicep()
            });

            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_NAME", typeof(string))
            {
                Value = containerRegistry.Name.ToBicepExpression()
            });

            // AZD looks for this output to find the container registry endpoint
            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_ENDPOINT", typeof(string))
            {
                Value = containerRegistry.LoginServer.ToBicepExpression()
            });

            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID", typeof(string))
            {
                Value = managedIdentityIdOutputValue
            });

            infra.Add(new ProvisioningOutput("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID", typeof(string))
            {
                Value = managedIdentityClientIdOutputValue
            });

            if (resource.EnableDashboard)
            {
                // Add aspire dashboard website
                var website = AzureAppServiceEnvironmentUtility.AddDashboard(infra, managedIdentityClientIdOutputValue, plan.Id);

                infra.Add(new ProvisioningOutput("AZURE_APP_SERVICE_DASHBOARD_URI", typeof(string))
                {
                    Value = BicepFunction.Interpolate($"https://{AzureAppServiceEnvironmentUtility.GetDashboardHostName(resource.Name)}.azurewebsites.net")
                });
            }

            if (resource.EnableApplicationInsights)
            {
                ApplicationInsightsComponent? applicationInsights = null;

                if (resource.ApplicationInsightsResource is not null)
                {
                    applicationInsights = (ApplicationInsightsComponent)resource.ApplicationInsightsResource.AddAsExistingResource(infra);
                }
                else
                {
                    // Create Log Analytics workspace
                    var logAnalyticsWorkspace = new OperationalInsightsWorkspace($"{prefix}_law")
                    {
                        Sku = new OperationalInsightsWorkspaceSku()
                        {
                            Name = OperationalInsightsWorkspaceSkuName.PerGB2018
                        }
                    };

                    infra.Add(logAnalyticsWorkspace);

                    // Create Application Insights resource linked to the Log Analytics workspace
                    applicationInsights = new ApplicationInsightsComponent($"{prefix}_ai")
                    {
                        ApplicationType = ApplicationInsightsApplicationType.Web,
                        Kind = "web",
                        WorkspaceResourceId = logAnalyticsWorkspace.Id,
                        IngestionMode = ComponentIngestionMode.LogAnalytics
                    };

                    if (resource.ApplicationInsightsLocation is not null)
                    {
                        var applicationInsightsLocation = new AzureLocation(resource.ApplicationInsightsLocation);
                        applicationInsights.Location = applicationInsightsLocation;
                    }
                    else if (resource.ApplicationInsightsLocationParameter is not null)
                    {
                        var applicationInsightsLocationParameter = resource.ApplicationInsightsLocationParameter.AsProvisioningParameter(infra);
                        applicationInsights.Location = applicationInsightsLocationParameter;
                    }
                }

                infra.Add(applicationInsights);

                infra.Add(new ProvisioningOutput("AZURE_APPLICATION_INSIGHTS_INSTRUMENTATIONKEY", typeof(string))
                {
                    Value = applicationInsights.InstrumentationKey.ToBicepExpression()
                });

                infra.Add(new ProvisioningOutput("AZURE_APPLICATION_INSIGHTS_CONNECTION_STRING", typeof(string))
                {
                    Value = applicationInsights.ConnectionString.ToBicepExpression()
                });
            }
        })
        {
            DefaultContainerRegistry = defaultRegistry
        };

        // Create the resource builder first, then attach the registry to avoid recreating builders
        var appServiceEnvBuilder = builder.ExecutionContext.IsPublishMode
            ? builder.AddResource(resource)
            : builder.CreateResourceBuilder(resource);

        return appServiceEnvBuilder;
    }

    /// <summary>
    /// Configures whether HTTP endpoints should be automatically upgraded to HTTPS for the Azure App Service environment.
    /// By default, HTTP endpoints are upgraded to HTTPS for security and WebSocket compatibility.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{AzureAppServiceEnvironmentResource}"/> to configure.</param>
    /// <param name="upgrade">Whether to upgrade HTTP endpoints to HTTPS. Default is true.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// When disabled (<c>false</c>), HTTP endpoints will use HTTP scheme and port 80 in Azure App Service.
    /// Note that Azure App Service forces HTTP to HTTPS redirects at the platform level,
    /// so disabling upgrade primarily affects connection strings generated for dependent resources.
    /// </remarks>
    /// <example>
    /// Preserve HTTP endpoints instead of automatically upgrading them to HTTPS:
    /// <code>
    /// var appService = builder.AddAzureAppServiceEnvironment("appservice")
    ///     .WithHttpsUpgrade(false);
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithHttpsUpgrade(this IResourceBuilder<AzureAppServiceEnvironmentResource> builder, bool upgrade = true)
    {
        builder.Resource.PreserveHttpEndpoints = !upgrade;
        return builder;
    }

    /// <summary>
    /// Configures whether the Aspire dashboard should be included in the Azure App Service environment.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{AzureAppServiceEnvironmentResource}"/> to configure.</param>
    /// <param name="enable">Whether to include the Aspire dashboard. Default is true.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithDashboard(this IResourceBuilder<AzureAppServiceEnvironmentResource> builder, bool enable = true)
    {
        builder.Resource.EnableDashboard = enable;
        return builder;
    }

    /// <summary>
    /// Configures whether Azure Application Insights should be enabled for the Azure App Service.
    /// </summary>
    /// <param name="builder">The AzureAppServiceEnvironmentResource to configure.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withAzureApplicationInsights dispatcher export.")]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithAzureApplicationInsights(this IResourceBuilder<AzureAppServiceEnvironmentResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Resource.EnableApplicationInsights = true;
        return builder;
    }

    /// <summary>
    /// Enables Azure Application Insights for the Azure App Service environment
    /// </summary>
    [AspireExport("withAzureApplicationInsights")]
    internal static IResourceBuilder<AzureAppServiceEnvironmentResource> WithAzureApplicationInsightsForPolyglot(
        this IResourceBuilder<AzureAppServiceEnvironmentResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>), typeof(IResourceBuilder<AzureApplicationInsightsResource>))] object? applicationInsights = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return applicationInsights switch
        {
            null => builder.WithAzureApplicationInsights(),
            string applicationInsightsLocation => builder.WithAzureApplicationInsights(applicationInsightsLocation),
            IResourceBuilder<ParameterResource> applicationInsightsLocationParameter => builder.WithAzureApplicationInsights(applicationInsightsLocationParameter),
            IResourceBuilder<AzureApplicationInsightsResource> applicationInsightsBuilder => builder.WithAzureApplicationInsights(applicationInsightsBuilder),
            _ => throw new ArgumentException(
                "Application Insights must be omitted, a location string, a location parameter, or an Application Insights resource builder.",
                nameof(applicationInsights))
        };
    }

    /// <summary>
    /// Configures whether Azure Application Insights should be enabled for the Azure App Service.
    /// </summary>
    /// <param name="builder">The AzureAppServiceEnvironmentResource to configure.</param>
    /// <param name="applicationInsightsLocation">The location for Application Insights.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withAzureApplicationInsights dispatcher export.")]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithAzureApplicationInsights(this IResourceBuilder<AzureAppServiceEnvironmentResource> builder, string applicationInsightsLocation)
    {
        builder.WithAzureApplicationInsights();
        builder.Resource.ApplicationInsightsLocation = applicationInsightsLocation;
        return builder;
    }

    /// <summary>
    /// Configures whether Azure Application Insights should be enabled for the Azure App Service.
    /// </summary>
    /// <param name="builder">The AzureAppServiceEnvironmentResource to configure.</param>
    /// <param name="applicationInsightsLocation">The location parameter for Application Insights.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withAzureApplicationInsights dispatcher export.")]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithAzureApplicationInsights(this IResourceBuilder<AzureAppServiceEnvironmentResource> builder, IResourceBuilder<ParameterResource> applicationInsightsLocation)
    {
        builder.WithAzureApplicationInsights();
        builder.Resource.ApplicationInsightsLocationParameter = applicationInsightsLocation.Resource;
        return builder;
    }

    /// <summary>
    /// Configures whether Azure Application Insights should be enabled for the Azure App Service.
    /// </summary>
    /// <param name="builder">The AzureAppServiceEnvironmentResource builder to configure.</param>
    /// <param name="applicationInsightsBuilder">The Application Insights resource builder.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withAzureApplicationInsights dispatcher export.")]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithAzureApplicationInsights(this IResourceBuilder<AzureAppServiceEnvironmentResource> builder, IResourceBuilder<AzureApplicationInsightsResource> applicationInsightsBuilder)
    {
        builder.WithAzureApplicationInsights();
        builder.Resource.ApplicationInsightsResource = applicationInsightsBuilder.Resource;
        return builder;
    }

    /// <summary>
    /// Configures the slot to which the Azure App Services should be deployed.
    /// </summary>
    /// <param name="builder">The AzureAppServiceEnvironmentResource to configure.</param>
    /// <param name="deploymentSlot">The deployment slot parameter for all App Services in the App Service Environment.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withDeploymentSlot dispatcher export.")]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithDeploymentSlot(this IResourceBuilder<AzureAppServiceEnvironmentResource> builder, IResourceBuilder<ParameterResource> deploymentSlot)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(deploymentSlot);

        builder.Resource.DeploymentSlotParameter = deploymentSlot.Resource;
        return builder;
    }

    /// <summary>
    /// Configures the deployment slot for all Azure App Services in the environment
    /// </summary>
    [AspireExport("withDeploymentSlot")]
    internal static IResourceBuilder<AzureAppServiceEnvironmentResource> WithDeploymentSlotForPolyglot(
        this IResourceBuilder<AzureAppServiceEnvironmentResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object deploymentSlot)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(deploymentSlot);

        return deploymentSlot switch
        {
            string deploymentSlotName => builder.WithDeploymentSlot(deploymentSlotName),
            IResourceBuilder<ParameterResource> deploymentSlotParameter => builder.WithDeploymentSlot(deploymentSlotParameter),
            _ => throw new ArgumentException("Deployment slot must be a string or a parameter resource builder.", nameof(deploymentSlot))
        };
    }

    /// <summary>
    /// Configures the slot to which the Azure App Services should be deployed.
    /// </summary>
    /// <param name="builder">The AzureAppServiceEnvironmentResource to configure.</param>
    /// <param name="deploymentSlot">The deployment slot for all App Services in the App Service Environment.</param>
    /// <returns><see cref="IResourceBuilder{T}"/></returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal withDeploymentSlot dispatcher export.")]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithDeploymentSlot(this IResourceBuilder<AzureAppServiceEnvironmentResource> builder, string deploymentSlot)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentSlot);

        builder.Resource.DeploymentSlot = deploymentSlot;
        return builder;
    }

    /// <summary>
    /// Configures the Azure App Service environment to use the supplied <see cref="AzureUserAssignedIdentityResource"/>
    /// as the managed identity that App Service apps use to pull images from the configured container registry
    /// (the <c>AcrPull</c> identity), instead of having Aspire create a new identity and a new <c>AcrPull</c>
    /// role assignment.
    /// </summary>
    /// <param name="builder">The Azure App Service environment to configure.</param>
    /// <param name="identityBuilder">
    /// The resource builder for the user-assigned identity that should be used for image pulls. The supplied
    /// identity must already have the <c>AcrPull</c> role on the configured container registry.
    /// </param>
    /// <returns>The <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// When this is set, Aspire will not create a new identity or an <c>AcrPull</c> role assignment for the
    /// container registry. The caller is responsible for ensuring the supplied identity already has the required
    /// <c>AcrPull</c> role assignment on the registry, for example by chaining
    /// <c>.WithRoleAssignments(acr, ContainerRegistryBuiltInRole.AcrPull)</c> when adding the identity.
    /// </para>
    /// <para>
    /// Unlike the equivalent method on Azure Container Apps, App Service reuses this single identity for more than
    /// just image pulls. The same identity is also attached to every generated App Service web app as a user-assigned
    /// managed identity, and is used by the Aspire OTLP sidecar to authenticate telemetry to the Aspire dashboard
    /// (it is added to the dashboard's <c>ALLOWED_MANAGED_IDENTITIES</c> and exposed to each app via the OTEL
    /// client-id app setting). If you supply an existing identity here, it will take on all of these roles.
    /// </para>
    /// <para>
    /// This is commonly combined with <c>AsExisting</c> on the App Service environment (App Service Plan) and on
    /// the container registry to deploy websites into a pre-provisioned set of Azure resources without Aspire
    /// emitting any new ACR-pull identity or ACR-pull role-assignment resources. See
    /// <see href="https://github.com/microsoft/aspire/issues/14382"/> for the scenario this addresses.
    /// </para>
    /// <para>
    /// Only the combination of an existing App Service Plan, an existing container registry, and this method
    /// avoids emitting any new identity or role-assignment resources in the env module. Other combinations still
    /// work but will emit additional resources:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   If the App Service Plan is marked <c>AsExisting</c> but no identity is supplied here, Aspire still emits
    ///   a new <c>UserAssignedIdentity</c> and an <c>AcrPull</c> role assignment on the configured registry.
    ///   </description></item>
    ///   <item><description>
    ///   If this method is used but the container registry is not existing (either the Aspire-generated default
    ///   registry or a user-added registry without <c>AsExisting</c>), the registry itself is still provisioned.
    ///   To wire the supplied identity to that newly-created registry, chain
    ///   <c>.WithRoleAssignments(acr, ContainerRegistryBuiltInRole.AcrPull)</c> on the identity.
    ///   </description></item>
    /// </list>
    /// <para>
    /// If the Aspire dashboard is enabled, Aspire still provisions the dashboard website and its contributor
    /// identity. Use <see cref="WithDashboard(IResourceBuilder{AzureAppServiceEnvironmentResource}, bool)"/> with
    /// <see langword="false"/> when targeting a fully pre-provisioned App Service Plan that should not receive a
    /// dashboard.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="identityBuilder"/> is <see langword="null"/>.</exception>
    [AspireExport]
    public static IResourceBuilder<AzureAppServiceEnvironmentResource> WithAcrPullIdentity(
        this IResourceBuilder<AzureAppServiceEnvironmentResource> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identityBuilder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identityBuilder);

        builder.WithAnnotation(new AzureAppServiceEnvironmentAcrPullIdentityAnnotation(identityBuilder.Resource));

        return builder;
    }

    private static AzureContainerRegistryResource CreateDefaultAzureContainerRegistry(IDistributedApplicationBuilder builder, string name)
    {
        var resource = new AzureContainerRegistryResource(name, ContainerRegistryInfrastructure.ConfigureContainerRegistry);
        if (builder.ExecutionContext.IsPublishMode)
        {
            builder.AddResource(resource);
        }
        return resource;
    }
}
