// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Search;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the Azure AI Search resources to the application model.
/// </summary>
public static class AzureSearchExtensions
{
    /// <summary>
    /// Adds an Azure AI Search service resource to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the Azure AI Search resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureSearchResource}"/>.</returns>
    /// <remarks>
    /// By default references to the Azure AI Search service resource will be assigned the following roles:
    /// 
    /// - <see cref="SearchBuiltInRole.SearchIndexDataContributor"/>
    /// - <see cref="SearchBuiltInRole.SearchServiceContributor"/>
    ///
    /// These can be replaced by calling <see cref="WithRoleAssignments{T}(IResourceBuilder{T}, IResourceBuilder{AzureSearchResource}, SearchBuiltInRole[])"/>.
    /// </remarks>
    public static IResourceBuilder<AzureSearchResource> AddAzureSearch(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        AzureSearchResource resource = new(name, ConfigureSearch);
        return builder.AddResource(resource)
            .WithDefaultRoleAssignments(SearchBuiltInRole.GetBuiltInRoleName,
                SearchBuiltInRole.SearchIndexDataContributor,
                SearchBuiltInRole.SearchServiceContributor);

        void ConfigureSearch(AzureResourceInfrastructure infrastructure)
        {
            var search = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = SearchService.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) => new SearchService(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    SearchSkuName = SearchServiceSkuName.Basic,
                    ReplicaCount = 1,
                    PartitionCount = 1,
                    HostingMode = SearchServiceHostingMode.Default,
                    IsLocalAuthDisabled = true,
                    Tags = { { "aspire-resource-name", name } }
                });

            // TODO: The endpoint format should move into Azure.Provisioning so we can maintain this
            // logic in a single location and have a better chance at supporting more than
            // just public Azure in the future.  https://github.com/Azure/azure-sdk-for-net/issues/42640
            infrastructure.Add(new ProvisioningOutput("connectionString", typeof(string))
            {
                Value = BicepFunction.Interpolate($"Endpoint=https://{search.Name}.search.windows.net")
            });

            infrastructure.Add(new ProvisioningOutput("endpoint", typeof(string))
            {
                Value = BicepFunction.Interpolate($"https://{search.Name}.search.windows.net")
            });

            // We need to output name to externalize role assignments.
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = search.Name.ToBicepExpression() });
        }
    }

    /// <summary>
    /// Assigns the specified roles to the given resource, granting it the necessary permissions
    /// on the target Azure AI Search service resource. This replaces the default role assignments for the resource.
    /// </summary>
    /// <param name="builder">The resource to which the specified roles will be assigned.</param>
    /// <param name="target">The target Azure AI Search service resource.</param>
    /// <param name="roles">The built-in AI Search roles to be assigned.</param>
    /// <returns>The updated <see cref="IResourceBuilder{T}"/> with the applied role assignments.</returns>
    /// <remarks>
    /// <example>
    /// Assigns the SearchIndexDataReader role to the 'Projects.Api' project.
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var search = builder.AddAzureSearch("search");
    /// 
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///   .WithRoleAssignments(search, SearchBuiltInRole.SearchIndexDataReader)
    ///   .WithReference(search);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureSearchResource> target,
        params SearchBuiltInRole[] roles)
        where T : IResource
    {
        return builder.WithRoleAssignments(target, SearchBuiltInRole.GetBuiltInRoleName, roles);
    }
}
