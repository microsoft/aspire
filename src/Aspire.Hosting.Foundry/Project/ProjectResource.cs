// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// The Microsoft Foundry project resource that can be used for Microsoft Foundry AI agents.
///
/// This also functions as an Aspire compute environment resource for deployment.
/// </summary>
public class AzureCognitiveServicesProjectResource :
    AzureProvisionableAspireResourceWithParent<CognitiveServicesProject, FoundryResource>,
    IResourceWithConnectionString,
    IAzureComputeEnvironmentResource
{
    /// <summary>
    /// Creates a new Microsoft Foundry project resource.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configureInfrastructure">Configures the underlying Azure resource using Azure.Provisioning.</param>
    /// <param name="parent">The parent Microsoft Foundry resource.</param>
    public AzureCognitiveServicesProjectResource([ResourceName] string name, Action<AzureResourceInfrastructure> configureInfrastructure, FoundryResource parent)
        : base(name, configureInfrastructure, parent)
    {
        // This is a bit long, but this sets up the necessary pipeline steps to
        // deploy hosted agents into this project. At a high level, that sequence is:
        // 1. (after project provisioning) Login to ACR (if needed)
        // 2. (for each compute resource) Build and push container image to ACR if needed
        // 3. For deployment target agent, deploy agent to project
        // 4. Mark deployment done
        Annotations.Add(new PipelineStepAnnotation(async (factoryContext) =>
        {
            var model = factoryContext.PipelineContext.Model;
            var steps = new List<PipelineStep>();

            var removeDefaultContainerRegistryStep = new PipelineStep
            {
                Name = $"prepare-foundry-project-{name}",
                Description = $"Prepares Microsoft Foundry project {name} for deployment.",
                Action = context =>
                {
                    if (this.HasAnnotationOfType<ContainerRegistryReferenceAnnotation>() &&
                        DefaultContainerRegistry is not null)
                    {
                        context.Model.Resources.Remove(DefaultContainerRegistry);
                        DefaultContainerRegistry = null;
                    }

                    return Task.CompletedTask;
                },
                Resource = this,
                DependsOnSteps = [AzureEnvironmentResource.PrepareResourcesStepName, WellKnownPipelineSteps.ValidateComputeEnvironments],
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
            };
            steps.Add(removeDefaultContainerRegistryStep);

            var computeUrls = new PipelineStep
            {
                Name = $"compute-endpoints-{name}",
                Action = async (context) =>
                {
                    var configuration = context.Services.GetRequiredService<IConfiguration>();
                    var subscriptionId = configuration["Azure:SubscriptionId"];
                    var resourceGroupName = configuration["Azure:ResourceGroup"];
                    if (string.IsNullOrEmpty(subscriptionId) || string.IsNullOrEmpty(resourceGroupName))
                    {
                        return;
                    }
                    var encodedSubscriptionId = EncodeSubscriptionId(subscriptionId);
                    // Print dashboard URL
                    await context.ReportingStep.CompleteAsync(
                        $"https://ai.azure.com/nextgen/r/{encodedSubscriptionId},{resourceGroupName},,{Parent.Name},{Name}/home").ConfigureAwait(false);
                },
                Resource = this,
                DependsOnSteps = [AzureEnvironmentResource.ProvisionInfrastructureStepName],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };
            steps.Add(computeUrls);
            return steps;
        }));
    }

    /// <inheritdoc/>
    public override void SetName(CognitiveServicesProject provisionableResource, BicepValue<string> name)
    {
        provisionableResource.Name = name;
    }

    /// <inheritdoc/>
    public override CognitiveServicesProject FromExisting(string bicepIdentifier)
    {
        return CognitiveServicesProject.FromExisting(bicepIdentifier);
    }

    /// <summary>
    /// Azure Resource ID output reference.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the .NET-style connection string for the project API endpoint.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"Endpoint={Endpoint}");

    /// <summary>
    /// Gets the Microsoft Foundry project endpoint as a ReferenceExpression that can be used in environment variables or connection strings.
    ///
    /// This will be used to instantiate the AI project clients.
    ///
    /// Will be of the format https:/{accountName}.services.ai.azure.com/api/projects/{projectName}?api-version={apiVersion}
    /// </summary>
    public ReferenceExpression UriExpression => ReferenceExpression.Create($"{Endpoint}");

    /// <summary>
    /// Gets the "endpoint" output reference from the Microsoft Foundry project resource.
    ///
    /// This will be used to instantiate the AI project clients.
    ///
    /// Will be of the format https:/{accountName}.services.ai.azure.com/api/projects/{projectName}?api-version={apiVersion}
    /// </summary>
    public BicepOutputReference Endpoint => new("endpoint", this);

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Uri", UriExpression);
        // Without this, `.WithReference(project)` causes an error like "waiting for connection string for resource {projectName}"
        yield return new("ConnectionString", ConnectionStringExpression);
        yield return new("ApplicationInsightsConnectionString", ReferenceExpression.Create($"{AppInsightsConnectionString}"));
    }

    /// <summary>
    /// Gets the managed identity principal ID, whether system-assigned or user-assigned.
    /// </summary>
    public BicepOutputReference PrincipalId => new("principalId", this);

    internal IReadOnlyList<CognitiveServicesBuiltInRole> ParentAccountRoleAssignments { get; set; } =
    [
        CognitiveServicesBuiltInRole.CognitiveServicesUser
    ];

    internal BicepOutputReference ContainerRegistryUrl => new("AZURE_CONTAINER_REGISTRY_ENDPOINT", this);
    internal BicepOutputReference ContainerRegistryName => new("AZURE_CONTAINER_REGISTRY_NAME", this);
    // Mnaged identity used for client access to container registry
    internal BicepOutputReference ContainerRegistryManagedIdentityId => new("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID", this);

    IAzureContainerRegistryResource? IAzureComputeEnvironmentResource.ContainerRegistry => ContainerRegistry;

    /// <summary>
    /// Gets the Azure Container Registry resource used by this project.
    /// </summary>
    public AzureContainerRegistryResource? ContainerRegistry
    {
        get
        {
            var registry = GetContainerRegistry();

            if (registry is null)
            {
                return null;
            }

            if (registry is not AzureContainerRegistryResource azureRegistry)
            {
                throw new InvalidOperationException(
                    $"The container registry configured for the Azure Cognitive Services project '{Name}' is not an Azure Container Registry. " +
                    $"Only Azure Container Registry resources are supported. Use '.WithAzureContainerRegistry()' to configure an Azure Container Registry.");
            }

            return azureRegistry;
        }
    }

    private IContainerRegistry? GetContainerRegistry()
    {
        if (this.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation))
        {
            return annotation.Registry;
        }
        return DefaultContainerRegistry;
    }

    /// <summary>
    /// The Application Insights resource associated with this project, if any.
    ///
    /// This will be used as the destination for server-side telemetry from hosted
    /// agents and optionally for client-side telemetry if the application decides
    /// to use it.
    /// </summary>
    public AzureApplicationInsightsResource? AppInsights { get; set; }

    internal BicepOutputReference AppInsightsConnectionString => new("APPLICATION_INSIGHTS_CONNECTION_STRING", this);

    /// <summary>
    /// Connection to the user-assigned key vault, if any.
    /// </summary>
    public AzureCognitiveServicesProjectConnectionResource? KeyVaultConn { get; set; }

    /// <summary>
    /// The capability host resources associated with this project, if any
    /// </summary>
    public CapabilityHostConfiguration? CapabilityHostConfiguration { get; set; }

    internal AzureContainerRegistryResource? DefaultContainerRegistry { get; set; }

    /// <summary>
    /// Get the address for the particular agent's endpoint.
    /// </summary>
    ReferenceExpression IComputeEnvironmentResource.GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;
        return ReferenceExpression.Create($"{Endpoint}/agents/{resource.Name}");
    }

    /// <summary>
    /// This is the encoding that the Microsoft Foundry web portal uses in their URLs, for some reason
    /// </summary>
    // Foundry portal URLs use big-endian (RFC 4122) GUID encoding, not .NET's mixed-endian ToByteArray().
    internal static string EncodeSubscriptionId(string subscriptionId)
    {
        var guid = Guid.Parse(subscriptionId);
        // Convert hex pairs from the canonical GUID string to bytes (preserves display order)
        var bytes = Convert.FromHexString(guid.ToString("N"));
        return Base64Url.EncodeToString(bytes).TrimEnd('=');
    }

    /// <summary>
    /// Tries to get the application identity resource associated with this project via
    /// the <see cref="AppIdentityAnnotation"/>. This is the identity that will be
    /// used by the project to access other Azure resources such as the container registry.
    /// </summary>
    public bool TryGetAppIdentityResource([NotNullWhen(true)] out IAppIdentityResource? identity)
    {
        if (this.TryGetLastAnnotation<AppIdentityAnnotation>(out var identityAnnotation))
        {
            identity = identityAnnotation.IdentityResource;
            return true;
        }
        else
        {
            identity = null;
            return false;
        }
    }
}

/// <summary>
/// Configuration for a Microsoft Foundry capability host.
/// </summary>
/// <param name="name">The name of the capability host.</param>
public class CapabilityHostConfiguration(string name)
{
    /// <summary>
    /// The name of the capability host.
    /// </summary>
    public string Name { get; set; } = name;

    /// <summary>
    /// The kind of capability host.
    /// </summary>
    public CapabilityHostKind Kind { get; set; } = CapabilityHostKind.Agents;

    /// <summary>
    /// The Cosmos DB resource to use for metadata and conversation state storage.
    /// </summary>
    public AzureCosmosDBResource? CosmosDB { get; set; }

    /// <summary>
    /// The Storage resource to use for file storage.
    /// </summary>
    public AzureStorageResource? Storage { get; set; }

    /// <summary>
    /// The Azure Search resource to use for vector search capabilities.
    /// </summary>
    public AzureSearchResource? Search { get; set; }

    /// <summary>
    /// An OpenAI-type Microsoft Foundry account to use for AI model calls, if any.
    /// </summary>
    public FoundryResource? AzureOpenAI { get; set; }

    internal void Validate(string projectName)
    {
        if (CosmosDB is null)
        {
            throw new InvalidOperationException($"Capability host '{Name}' on project '{projectName}' requires a CosmosDB resource. Call WithCosmosDB() on the capability host builder.");
        }
        if (Storage is null)
        {
            throw new InvalidOperationException($"Capability host '{Name}' on project '{projectName}' requires a Storage resource. Call WithStorage() on the capability host builder.");
        }
        if (Search is null)
        {
            throw new InvalidOperationException($"Capability host '{Name}' on project '{projectName}' requires a Search resource. Call WithSearch() on the capability host builder.");
        }
    }
}

/// <summary>
/// A fluent builder for configuring a capability host on a Microsoft Foundry project.
/// </summary>
public class CapabilityHostBuilder(IResourceBuilder<AzureCognitiveServicesProjectResource> projectBuilder, CapabilityHostConfiguration configuration)
{
    /// <summary>
    /// Gets the underlying project resource builder.
    /// </summary>
    public IResourceBuilder<AzureCognitiveServicesProjectResource> ProjectBuilder { get; } = projectBuilder;

    /// <summary>
    /// Configures the Cosmos DB resource for metadata and conversation state storage.
    /// </summary>
    /// <param name="cosmosDb">The Cosmos DB resource builder.</param>
    /// <returns>The capability host builder for chaining.</returns>
    public CapabilityHostBuilder WithCosmosDB(IResourceBuilder<AzureCosmosDBResource> cosmosDb)
    {
        ArgumentNullException.ThrowIfNull(cosmosDb);
        configuration.CosmosDB = cosmosDb.Resource;
        return this;
    }

    /// <summary>
    /// Configures the Storage resource for file storage.
    /// </summary>
    /// <param name="storage">The Storage resource builder.</param>
    /// <returns>The capability host builder for chaining.</returns>
    public CapabilityHostBuilder WithStorage(IResourceBuilder<AzureStorageResource> storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        configuration.Storage = storage.Resource;
        return this;
    }

    /// <summary>
    /// Configures the Azure Search resource for vector search capabilities.
    /// </summary>
    /// <param name="search">The Search resource builder.</param>
    /// <returns>The capability host builder for chaining.</returns>
    public CapabilityHostBuilder WithSearch(IResourceBuilder<AzureSearchResource> search)
    {
        ArgumentNullException.ThrowIfNull(search);
        configuration.Search = search.Resource;
        return this;
    }

    /// <summary>
    /// Configures an Azure OpenAI-type Microsoft Foundry account for AI model calls.
    /// </summary>
    /// <param name="openAI">The Microsoft Foundry resource builder.</param>
    /// <returns>The capability host builder for chaining.</returns>
    public CapabilityHostBuilder WithAzureOpenAI(IResourceBuilder<FoundryResource> openAI)
    {
        ArgumentNullException.ThrowIfNull(openAI);
        configuration.AzureOpenAI = openAI.Resource;
        return this;
    }
}
