// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Foundry;
using Aspire.Hosting.Eventing;
using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using static Azure.Provisioning.Expressions.BicepFunction;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Microsoft Foundry resources to the application model.
/// </summary>
public static class FoundryExtensions
{
    private const string DefaultCapabilityHostName = "foundry-caphost";
    internal const string LocalProjectsNotSupportedMessage = "Microsoft Foundry projects are not supported when the parent Foundry resource is configured with RunAsFoundryLocal().";
    private static readonly TimeSpan s_healthCheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Adds a Microsoft Foundry resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<FoundryResource> AddFoundry(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        builder.AddAzureProvisioning();

        var resource = new FoundryResource(name, ConfigureInfrastructure);
        return builder.AddResource(resource)
            .WithIconName("AgentsAdd")
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
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addDeployment dispatcher export.")]
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

        return deploymentBuilder.WithIconName("BoxMultiple");
    }

    /// <summary>
    /// Adds a Microsoft Foundry deployment resource to a Microsoft Foundry resource.
    /// </summary>
    [AspireExport("addDeployment")]
    internal static IResourceBuilder<FoundryDeploymentResource> AddDeploymentForPolyglot(
        this IResourceBuilder<FoundryResource> builder,
        [ResourceName] string name,
        [AspireUnion(typeof(FoundryModel), typeof(string))] object model,
        string? modelVersion = null,
        string? format = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return model switch
        {
            FoundryModel foundryModel when modelVersion is null && format is null => builder.AddDeployment(name, foundryModel),
            FoundryModel => throw new ArgumentException("Model version and format must be omitted when using a FoundryModel.", nameof(modelVersion)),
            string modelName when modelVersion is not null && format is not null => builder.AddDeployment(name, modelName, modelVersion, format),
            string => throw new ArgumentException("Model version and format are required when the model is provided as a string.", nameof(modelVersion)),
            _ => throw new ArgumentException("Model must be a FoundryModel or a string model name.", nameof(model))
        };
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
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addDeployment dispatcher export.")]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withFoundryDeploymentProperties", MethodName = "withProperties", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<FoundryDeploymentResource> WithProperties(this IResourceBuilder<FoundryDeploymentResource> builder, Action<FoundryDeploymentResource> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        configure(builder.Resource);

        return builder;
    }

    /// <summary>
    /// Configures a Microsoft Foundry resource to use an Aspire-managed Foundry Local service.
    /// </summary>
    /// <param name="builder">The Microsoft Foundry resource builder.</param>
    /// <returns>The configured Microsoft Foundry resource builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    [AspireExportIgnore(Reason = "Binary compatibility overload. Polyglot app hosts use the overload with the optional endpoint.")]
    public static IResourceBuilder<FoundryResource> RunAsFoundryLocal(this IResourceBuilder<FoundryResource> builder)
        => builder.RunAsFoundryLocal(endpoint: null);

    /// <summary>
    /// Configures a Microsoft Foundry resource to use an Aspire-managed or existing Foundry Local service.
    /// </summary>
    /// <param name="builder">The Microsoft Foundry resource builder.</param>
    /// <param name="endpoint">The endpoint of an existing Foundry Local service, or <see langword="null"/> for an Aspire-managed service.</param>
    /// <returns>The configured Microsoft Foundry resource builder.</returns>
    /// <remarks>
    /// When <paramref name="endpoint"/> is provided, Aspire connects to that existing
    /// Foundry Local service without starting, stopping, downloading, or loading anything on its host.
    /// Models configured on the resource must already be loaded by the existing service.
    /// </remarks>
    /// <example>
    /// Connect to an existing Foundry Local service and identify the model that is already loaded:
    /// <code lang="csharp">
    /// var foundry = builder.AddFoundry("foundry")
    ///     .RunAsFoundryLocal("http://windows-host:5273");
    ///
    /// var chat = foundry.AddDeployment("chat", FoundryModel.Local.Phi4Mini)
    ///     .WithProperties(deployment =>
    ///     {
    ///         deployment.LocalModelId = "Phi-4-mini-instruct-generic-gpu:5";
    ///     });
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is not an absolute HTTP or HTTPS URL.</exception>
    [AspireExport]
    public static IResourceBuilder<FoundryResource> RunAsFoundryLocal(
        this IResourceBuilder<FoundryResource> builder,
        string? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        Uri? existingEndpoint = null;
        if (endpoint is not null)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out existingEndpoint) ||
                existingEndpoint.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("The Foundry Local endpoint must be an absolute HTTP or HTTPS URL.", nameof(endpoint));
            }

            existingEndpoint = EnsureTrailingSlash(existingEndpoint);
        }

        var resource = builder.Resource;
        ThrowIfProjectsConfiguredForLocal(builder, resource);
        resource.Annotations.Add(new EmulatorResourceAnnotation());
        resource.ApiKey = FoundryLocalService.ApiKey;
        resource.EmulatorServiceUri = existingEndpoint;
        resource.ManageLocalService = existingEndpoint is null;

        builder.WithInitializer();
        if (resource.ManageLocalService)
        {
            builder.OnResourceStopped(static (_, _, ct) => FoundryLocalService.StopAsync(ct));
            builder.ApplicationBuilder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FoundryLocalLifecycleService>());
        }

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
                    sp => new FoundryLocalHealthCheck(resource, sp.GetRequiredService<IHttpClientFactory>()),
                    failureStatus: default,
                    tags: default,
                    timeout: default
                    ));
        builder.ApplicationBuilder.Services.AddHttpClient(nameof(FoundryLocalHealthCheck), client =>
            client.Timeout = s_healthCheckTimeout);
        builder.ApplicationBuilder.Services.AddHttpClient(nameof(LocalModelHealthCheck), client =>
            client.Timeout = s_healthCheckTimeout);

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }

    internal static void ThrowIfProjectsConfiguredForLocal(IResourceBuilder<FoundryResource> builder, FoundryResource resource)
    {
        if (builder.ApplicationBuilder.Resources
            .OfType<AzureCognitiveServicesProjectResource>()
            .Any(project => ReferenceEquals(project.Parent, resource)))
        {
            throw new InvalidOperationException(LocalProjectsNotSupportedMessage);
        }
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
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentException">Thrown when a role value is not a valid <see cref="FoundryRole"/> value.</exception>
    [AspireExport("withFoundryRoleAssignments")]
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
                var logger = @event.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource);

                await rns.PublishUpdateAsync(resource, state => state with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Info)
                }).ConfigureAwait(false);

                try
                {
                    if (resource.ManageLocalService)
                    {
                        await FoundryLocalService.StartAsync(logger, ct).ConfigureAwait(false);
                        resource.EmulatorServiceUri = FoundryLocalService.Endpoint;
                    }
                }
                catch (Exception e)
                {
                    logger.LogInformation("Foundry Local could not be started. Ensure it's installed correctly: https://learn.microsoft.com/azure/ai-foundry/foundry-local/get-started (Error: {Error}).", e.Message);
                }

                if (resource.EmulatorServiceUri is not null)
                {
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
            var eventing = @event.Services.GetRequiredService<IDistributedApplicationEventing>();

            var model = deployment.ModelName;
            var manageModel = foundryResource.ManageLocalService;

            _ = Task.Run(async () =>
            {
                try
                {
                    await rns.PublishUpdateAsync(deployment, state => state with
                    {
                        State = new ResourceStateSnapshot(manageModel ? $"Preparing model {model}" : $"Using existing model {model}", KnownResourceStateStyles.Info),
                        Properties = [.. state.Properties, new(CustomResourceKnownProperties.Source, model)]
                    }).ConfigureAwait(false);

                    if (manageModel)
                    {
                        var requestedModel = deployment.LocalModelId ?? model;
                        var cachedModelId = await FoundryLocalService.TryLoadCachedModelAsync(requestedModel, ct).ConfigureAwait(false);

                        if (cachedModelId is not null)
                        {
                            deployment.LocalModelId = cachedModelId;
                        }
                        else
                        {
                            deployment.LocalModelId = await DownloadModelAsync(requestedModel).ConfigureAwait(false);

                            await rns.PublishUpdateAsync(deployment, state => state with
                            {
                                State = new ResourceStateSnapshot("Loading model", KnownResourceStateStyles.Info)
                            }).ConfigureAwait(false);

                            await FoundryLocalService.LoadModelAsync(deployment.LocalModelId, ct).ConfigureAwait(false);
                        }

                        logger.LogInformation("Model {Model} is loaded ({ModelId}).", model, deployment.LocalModelId);
                    }
                    else
                    {
                        deployment.LocalModelId ??= model;
                    }

                    // Re-publish the connection string since the model id is now known.
                    var connectionStringAvailableEvent = new ConnectionStringAvailableEvent(deployment, @event.Services);
                    await eventing.PublishAsync(connectionStringAvailableEvent, ct).ConfigureAwait(false);

                    await rns.PublishUpdateAsync(deployment, state => state with
                    {
                        State = KnownResourceStates.Running,
                        Properties = [.. state.Properties, new(CustomResourceKnownProperties.Source, $"{model} ({deployment.LocalModelId})")]
                    }).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    logger.LogInformation("Failed to start {Model}. Error: {Error}", model, e.Message);

                    await rns.PublishUpdateAsync(deployment, state => state with
                    {
                        State = KnownResourceStates.FailedToStart
                    }).ConfigureAwait(false);
                }

                async Task<string> DownloadModelAsync(string requestedModel)
                {
                    await rns.PublishUpdateAsync(deployment, state => state with
                    {
                        State = new ResourceStateSnapshot($"Downloading model {model}", KnownResourceStateStyles.Info)
                    }).ConfigureAwait(false);

                    var progressChannel = Channel.CreateUnbounded<float>();
                    var downloadTask = DownloadWithProgressAsync();

                    await foreach (var progress in progressChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        logger.LogInformation("Downloading model {Model}: {Progress:F2}%", model, progress);
                        await rns.PublishUpdateAsync(deployment, state => state with
                        {
                            State = new ResourceStateSnapshot($"Downloading model {model}: {progress:F2}%", KnownResourceStateStyles.Info)
                        }).ConfigureAwait(false);
                    }

                    return await downloadTask.ConfigureAwait(false);

                    async Task<string> DownloadWithProgressAsync()
                    {
                        try
                        {
                            return await FoundryLocalService.DownloadModelAsync(
                                requestedModel,
                                progress => progressChannel.Writer.TryWrite(progress),
                                ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            progressChannel.Writer.TryComplete();
                        }
                    }
                }
            }, ct);

            return Task.CompletedTask;
        });

        var healthCheckKey = $"{deployment.Name}_check";

        builder.ApplicationBuilder.Services.AddHealthChecks()
                .Add(new HealthCheckRegistration(
                    healthCheckKey,
                    sp => new LocalModelHealthCheck(deployment, sp.GetRequiredService<IHttpClientFactory>()),
                    failureStatus: default,
                    tags: default,
                    timeout: default
                    ));

        builder.WithHealthCheck(healthCheckKey);

        return builder;
    }

    private static Uri EnsureTrailingSlash(Uri endpoint)
    {
        if (endpoint.AbsolutePath.EndsWith('/'))
        {
            return endpoint;
        }

        var builder = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath + "/"
        };

        return builder.Uri;
    }

    private static void ConfigureInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var azureResource = (FoundryResource)infrastructure.AspireResource;

        // Check if this Foundry resource has a private endpoint (via annotation)
        var hasPrivateEndpoint = azureResource.HasAnnotationOfType<PrivateEndpointTargetAnnotation>();

        var cogServicesAccount = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = CognitiveServicesAccount.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) =>
                {
                    // Cognitive Services account names are limited to 64 characters; reserve room for the unique suffix.
                    var accountNamePrefix = infrastructure.AspireResource.Name[..Math.Min(infrastructure.AspireResource.Name.Length, 50)];
                    var accountName = ToLower(Interpolate($"{accountNamePrefix}-{GetUniqueString(GetResourceGroup().Id)}"));

                    return new CognitiveServicesAccount(infrastructure.AspireResource.GetBicepIdentifier())
                    {
                        Name = accountName,
                        Kind = "AIServices",
                        Sku = new CognitiveServicesSku()
                        {
                            Name = "S0"
                        },
                        Properties = new CognitiveServicesAccountProperties()
                        {
                            CustomSubDomainName = accountName,
                            PublicNetworkAccess = hasPrivateEndpoint
                                ? ServiceAccountPublicNetworkAccess.Disabled
                                : ServiceAccountPublicNetworkAccess.Enabled,
                            DisableLocalAuth = true,
                            AllowProjectManagement = true
                        },
                        Identity = new ManagedServiceIdentity()
                        {
                            ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned
                        },
                        Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                    };
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

        infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = cogServicesAccount.Id.ToBicepExpression() });

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
