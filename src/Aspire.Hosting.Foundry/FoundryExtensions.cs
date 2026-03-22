// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;
using Aspire.Hosting.Eventing;
using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using static Azure.Provisioning.Expressions.BicepFunction;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Microsoft Foundry resources to the application model.
/// </summary>
public static class FoundryExtensions
{
    private const string DefaultCapabilityHostName = "foundry-caphost";

    /// <summary>
    /// Adds a Microsoft Foundry resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addFoundry", Description = "Adds a Microsoft Foundry resource to the distributed application model.")]
    public static IResourceBuilder<FoundryResource> AddFoundry(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        builder.AddAzureProvisioning();

        var resource = new FoundryResource(name, ConfigureInfrastructure);
        return builder.AddResource(resource)
            .WithDefaultRoleAssignments(CognitiveServicesBuiltInRole.GetBuiltInRoleName,
                CognitiveServicesBuiltInRole.CognitiveServicesUser, CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser);
    }

    /// <summary>
    /// Adds and returns a Microsoft Foundry Deployment resource (e.g. an AI model) to the application model.
    /// </summary>
    /// <param name="builder">The Microsoft Foundry resource builder.</param>
    /// <param name="name">The name of the Microsoft Foundry Deployment resource.</param>
    /// <param name="modelName">The name of the model to deploy.</param>
    /// <param name="modelVersion">The version of the model to deploy.</param>
    /// <param name="format">The format of the model to deploy.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addDeployment", Description = "Adds a Microsoft Foundry deployment resource to a Microsoft Foundry resource.")]
    public static IResourceBuilder<FoundryDeploymentResource> AddDeployment(this IResourceBuilder<FoundryResource> builder, [ResourceName] string name, string modelName, string modelVersion, string format)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(modelName);
        ArgumentException.ThrowIfNullOrEmpty(modelVersion);
        ArgumentException.ThrowIfNullOrEmpty(format);

        var deployment = new FoundryDeploymentResource(name, modelName, modelVersion, format, builder.Resource);

        builder.ApplicationBuilder.AddResource(deployment);

        builder.Resource.AddDeployment(deployment);

        var deploymentBuilder = builder.ApplicationBuilder
            .CreateResourceBuilder(deployment);

        if (builder.Resource.IsEmulator)
        {
            deploymentBuilder.AsLocalDeployment(deployment);
        }

        return deploymentBuilder;
    }

    /// <summary>
    /// Adds and returns a Microsoft Foundry Deployment resource to the application model using a <see cref="FoundryModel"/>.
    /// </summary>
    /// <param name="builder">The Microsoft Foundry resource builder.</param>
    /// <param name="name">The name of the Microsoft Foundry Deployment resource.</param>
    /// <param name="model">The model descriptor, using the <see cref="FoundryModel"/> class like so: <code lang="csharp">aiFoundry.AddDeployment(name: "chat", model: FoundryModel.OpenAI.Gpt5Mini)</code></param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <example>
    /// Create a deployment for the OpenAI GTP-5-mini model:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var aiFoundry = builder.AddFoundry("aiFoundry");
    /// var gpt5mini = aiFoundry.AddDeployment("chat", FoundryModel.OpenAI.Gpt5Mini);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport("addDeploymentFromModel", Description = "Adds a Microsoft Foundry deployment resource by using a Microsoft Foundry model descriptor.")]
    public static IResourceBuilder<FoundryDeploymentResource> AddDeployment(this IResourceBuilder<FoundryResource> builder, [ResourceName] string name, FoundryModel model)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(model.Name);
        ArgumentException.ThrowIfNullOrEmpty(model.Version);
        ArgumentException.ThrowIfNullOrEmpty(model.Format);

        return builder.AddDeployment(name, model.Name, model.Version, model.Format);
    }

    /// <summary>
    /// Allows setting the properties of a Microsoft Foundry Deployment resource.
    /// </summary>
    /// <param name="builder">The Microsoft Foundry Deployment resource builder.</param>
    /// <param name="configure">A method that can be used for customizing the <see cref="FoundryDeploymentResource"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withFoundryDeploymentProperties", MethodName = "withProperties", Description = "Configures properties of a Microsoft Foundry deployment resource.", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<FoundryDeploymentResource> WithProperties(this IResourceBuilder<FoundryDeploymentResource> builder, Action<FoundryDeploymentResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);

        return builder;
    }

    /// <summary>
    /// Adds a Foundry Local resource to the distributed application builder.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <returns>A resource builder for the Foundry Local resource.</returns>
    [AspireExport("runAsFoundryLocal", Description = "Configures the Microsoft Foundry resource to run by using Foundry Local.")]
    public static IResourceBuilder<FoundryResource> RunAsFoundryLocal(this IResourceBuilder<FoundryResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder, nameof(builder));

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var resource = builder.Resource;
        resource.Annotations.Add(new EmulatorResourceAnnotation());

        builder.ApplicationBuilder.Services.AddSingleton<FoundryLocalManager>();

        builder.WithInitializer();

        foreach (var deployment in resource.Deployments)
        {
            var deploymentBuilder = builder.ApplicationBuilder
                .CreateResourceBuilder(deployment);

            deploymentBuilder.AsLocalDeployment(deployment);
        }

        var healthCheckKey = $"{resource.Name}_check";
        builder.ApplicationBuilder.Services.AddHealthChecks()
                .Add(new HealthCheckRegistration(
                    healthCheckKey,
                    sp => new FoundryLocalHealthCheck(sp.GetRequiredService<FoundryLocalManager>()),
                    failureStatus: default,
                    tags: default,
                    timeout: default
                    ));

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Microsoft Foundry resource. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Microsoft Foundry resource.</param>
    /// <param name="roles">The built-in Cognitive Services roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <remarks>
    /// <example>
    /// Assigns the CognitiveServicesOpenAIContributor role to the 'Projects.Api' project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var aiFoundry = builder.AddFoundry("aiFoundry");
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithRoleAssignments(aiFoundry, CognitiveServicesBuiltInRole.CognitiveServicesOpenAIContributor)
    ///   .WithReference(aiFoundry);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "CognitiveServicesBuiltInRole is an Azure.Provisioning type not compatible with ATS. Use the FoundryRole-based overload instead.")]
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<FoundryResource> target,
        params CognitiveServicesBuiltInRole[] roles)
        where T : IResource
    {
        return builder.WithRoleAssignments(target, CognitiveServicesBuiltInRole.GetBuiltInRoleName, roles);
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Microsoft Foundry resource. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Microsoft Foundry resource.</param>
    /// <param name="roles">The Microsoft Foundry roles to be assigned (for example, <see cref="FoundryRole.CognitiveServicesOpenAIUser"/>).</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <exception cref="ArgumentException">Thrown when a role value is not a valid <see cref="FoundryRole"/> value.</exception>
    [AspireExport("withFoundryRoleAssignments", MethodName = "withRoleAssignments", Description = "Assigns Microsoft Foundry roles to a resource")]
    internal static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<FoundryResource> target,
        params FoundryRole[] roles)
        where T : IResource
    {
        if (roles is null || roles.Length == 0)
        {
            return builder.WithRoleAssignments(target, Array.Empty<CognitiveServicesBuiltInRole>());
        }

        var builtInRoles = new CognitiveServicesBuiltInRole[roles.Length];
        for (var i = 0; i < roles.Length; i++)
        {
            builtInRoles[i] = roles[i] switch
            {
                FoundryRole.CognitiveServicesOpenAIContributor => CognitiveServicesBuiltInRole.CognitiveServicesOpenAIContributor,
                FoundryRole.CognitiveServicesOpenAIUser => CognitiveServicesBuiltInRole.CognitiveServicesOpenAIUser,
                FoundryRole.CognitiveServicesUser => CognitiveServicesBuiltInRole.CognitiveServicesUser,
                _ => throw new ArgumentException($"'{roles[i]}' is not a valid {nameof(FoundryRole)} value.", nameof(roles))
            };
        }

        return builder.WithRoleAssignments(target, builtInRoles);
    }

    private static IResourceBuilder<FoundryResource> WithInitializer(this IResourceBuilder<FoundryResource> builder)
    {
        return builder.OnInitializeResource((resource, @event, ct)
            => Task.Run(async () =>
            {
                var rns = @event.Services.GetRequiredService<ResourceNotificationService>();
                var manager = @event.Services.GetRequiredService<FoundryLocalManager>();
                var logger = @event.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource);

                resource.ApiKey = manager.ApiKey;

                await rns.PublishUpdateAsync(resource, state => state with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info)
                }).ConfigureAwait(false);

                try
                {
                    await manager.StartServiceAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    logger.LogInformation("Foundry Local could not be started. Ensure it's installed correctly: https://learn.microsoft.com/azure/ai-foundry/foundry-local/get-started (Error: {Error}).", e.Message);
                }

                if (manager.IsServiceRunning)
                {
                    resource.EmulatorServiceUri = manager.Endpoint;

                    await rns.PublishUpdateAsync(resource, state => state with
                    {
                        State = KnownResourceStates.Running,
                        Properties = [.. state.Properties, new(CustomResourceKnownProperties.Source, "Foundry Local")]
                    }).ConfigureAwait(false);
                }
                else
                {
                    await rns.PublishUpdateAsync(resource, state => state with
                    {
                        State = KnownResourceStates.FailedToStart,
                        Properties = [.. state.Properties, new(CustomResourceKnownProperties.Source, "Foundry Local")]
                    }).ConfigureAwait(false);
                }

            }, ct));
    }

    /// <summary>
    /// Configure a deployment for use with Foundry Local
    /// </summary>
    internal static IResourceBuilder<FoundryDeploymentResource> AsLocalDeployment(this IResourceBuilder<FoundryDeploymentResource> builder, FoundryDeploymentResource deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment, nameof(deployment));

        var foundryResource = builder.Resource.Parent;
        builder.ApplicationBuilder.Eventing.Subscribe<ResourceReadyEvent>(foundryResource, (@event, ct) =>
        {
            var rns = @event.Services.GetRequiredService<ResourceNotificationService>();
            var loggerService = @event.Services.GetRequiredService<ResourceLoggerService>();
            var logger = loggerService.GetLogger(deployment);
            var manager = @event.Services.GetRequiredService<FoundryLocalManager>();
            var eventing = @event.Services.GetRequiredService<IDistributedApplicationEventing>();

            var model = deployment.ModelName;

            _ = Task.Run(async () =>
            {
                await rns.PublishUpdateAsync(deployment, state => state with
                {
                    State = new ResourceStateSnapshot($"Downloading model {model}", KnownResourceStateStyles.Info),
                    Properties = [.. state.Properties, new(CustomResourceKnownProperties.Source, model)]
                }).ConfigureAwait(false);

                var result = manager.DownloadModelWithProgressAsync(model, ct: ct);

                await foreach (var progress in result.ConfigureAwait(false))
                {
                    if (progress.IsCompleted && progress.ModelInfo is not null)
                    {
                        // Set the model id that was actually downloaded. This is the value that is used in the
                        // connection string

                        deployment.ModelId = progress.ModelInfo.ModelId;
                        logger.LogInformation("Model {Model} downloaded successfully ({ModelId}).", model, deployment.ModelId);

                        // Re-publish the connection string since the model id is now known
                        var connectionStringAvailableEvent = new ConnectionStringAvailableEvent(deployment, @event.Services);
                        await eventing.PublishAsync(connectionStringAvailableEvent, ct).ConfigureAwait(false);

                        await rns.PublishUpdateAsync(deployment, state => state with
                        {
                            Properties = [.. state.Properties, new(CustomResourceKnownProperties.Source, $"{model} ({deployment.ModelId})")]
                        }).ConfigureAwait(false);

                        await rns.PublishUpdateAsync(deployment, state => state with
                        {
                            State = new ResourceStateSnapshot("Loading model", KnownResourceStateStyles.Info)
                        }).ConfigureAwait(false);

                        try
                        {
                            _ = await manager.LoadModelAsync(deployment.ModelId, ct: ct).ConfigureAwait(false);

                            await rns.PublishUpdateAsync(deployment, state => state with
                            {
                                State = KnownResourceStates.Running
                            }).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            // LoadModelAsync throws IOE when the model is invalid.
                            logger.LogInformation("Failed to start {Model}. Error: {Error}", model, e.Message);

                            await rns.PublishUpdateAsync(deployment, state => state with
                            {
                                State = KnownResourceStates.FailedToStart
                            }).ConfigureAwait(false);
                        }
                    }
                    else if (progress.IsCompleted && !string.IsNullOrEmpty(progress.ErrorMessage))
                    {
                        logger.LogInformation("Failed to start {Model}. Error: {Error}", model, progress.ErrorMessage);
                        await rns.PublishUpdateAsync(deployment, state => state with
                        {
                            State = KnownResourceStates.FailedToStart
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogInformation("Downloading model {Model}: {Progress:F2}%", model, progress.Percentage);
                        await rns.PublishUpdateAsync(deployment, state => state with
                        {
                            State = new ResourceStateSnapshot($"Downloading model {model}: {progress.Percentage:F2}%", KnownResourceStateStyles.Info)
                        }).ConfigureAwait(false);
                    }
                }
            }, ct);

            return Task.CompletedTask;
        });

        var healthCheckKey = $"{deployment.Name}_check";

        builder.ApplicationBuilder.Services.AddHealthChecks()
                .Add(new HealthCheckRegistration(
                    healthCheckKey,
                    sp => new LocalModelHealthCheck(modelId: deployment.ModelId, sp.GetRequiredService<FoundryLocalManager>()),
                    failureStatus: default,
                    tags: default,
                    timeout: default
                    ));

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }

    private static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var cogServicesAccount = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = CognitiveServicesAccount.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) => new CognitiveServicesAccount(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Kind = "AIServices",
                    Sku = new CognitiveServicesSku()
                    {
                        Name = "S0"
                    },
                    Properties = new CognitiveServicesAccountProperties()
                    {
                        // Until this bug is fixed, CustomSubDomainName must be set to the
                        // account's name: https://msdata.visualstudio.com/Vienna/_workitems/edit/4866592
                        CustomSubDomainName = ToLower(Take(Concat(infrastructure.AspireResource.Name, GetUniqueString(GetResourceGroup().Id)), 24)),
                        PublicNetworkAccess = ServiceAccountPublicNetworkAccess.Enabled,
                        DisableLocalAuth = true,
                        AllowProjectManagement = true
                    },
                    Identity = new ManagedServiceIdentity()
                    {
                        ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned
                    },
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                });

        infrastructure.Add(new ProvisioningOutput("aiFoundryApiEndpoint", typeof(string))
        {
            Value = (BicepValue<string>)new IndexExpression(
                (BicepExpression)cogServicesAccount.Properties.Endpoints!,
                "AI Foundry API")
        });

        infrastructure.Add(new ProvisioningOutput("endpoint", typeof(string))
        {
            Value = cogServicesAccount.Properties.Endpoint.ToBicepExpression()
        });

        infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = cogServicesAccount.Name.ToBicepExpression() });

        var resource = (FoundryResource)infrastructure.AspireResource;

        if (resource.CapabilityHost != null)
        {
            // Use the specified capability host
            resource.CapabilityHost.Parent = cogServicesAccount;
            infrastructure.Add(resource.CapabilityHost);
        }
        else
        {
            // Provision a default capability host for hosted agents
            var capHost = new CognitiveServicesCapabilityHost(Infrastructure.NormalizeBicepIdentifier($"{resource.Name}-caphost"), "2025-10-01-preview")
            {
                Name = DefaultCapabilityHostName,
                Parent = cogServicesAccount,
                // IMPORTANT: this is required to enable hosted agents deployment
                // if no BYO Net is provided
                Properties = new PublicHostingCognitiveServicesCapabilityHostProperties()
                {
                    CapabilityHostKind = CapabilityHostKind.Agents
                }
            };
            infrastructure.Add(capHost);
            resource.CapabilityHost = capHost;
        }

        CognitiveServicesAccountDeployment? dependency = null;
        foreach (var deployment in resource.Deployments)
        {
            var cdkDeployment = new CognitiveServicesAccountDeployment(Infrastructure.NormalizeBicepIdentifier(deployment.Name))
            {
                Name = deployment.DeploymentName,
                Parent = cogServicesAccount,
                Properties = new CognitiveServicesAccountDeploymentProperties()
                {
                    Model = new CognitiveServicesAccountDeploymentModel()
                    {
                        Name = deployment.ModelName,
                        Version = deployment.ModelVersion,
                        Format = deployment.Format
                    }
                },
                Sku = new CognitiveServicesSku()
                {
                    Name = deployment.SkuName,
                    Capacity = deployment.SkuCapacity
                }
            };
            infrastructure.Add(cdkDeployment);

            // Subsequent deployments need an explicit dependency on the previous one
            // to ensure they are not created in parallel. This is equivalent to @batchSize(1)
            // which can't be defined with the CDK

            if (dependency != null)
            {
                cdkDeployment.DependsOn.Add(dependency);
            }

            dependency = cdkDeployment;
        }
    }

}
