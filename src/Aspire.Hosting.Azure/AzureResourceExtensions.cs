// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure resources to the application model.
/// </summary>
public static class AzureResourceExtensions
{
    /// <summary>
    /// Changes the resource to be published as a connection string reference in the manifest.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The configured <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Publishes an Azure resource to the manifest as a connection string")]
    public static IResourceBuilder<T> PublishAsConnectionString<T>(this IResourceBuilder<T> builder)
        where T : IAzureResource, IResourceWithConnectionString
    {
        ParameterResourceBuilderExtensions.ConfigureConnectionStringManifestPublisher((IResourceBuilder<IResourceWithConnectionString>)builder);
        return builder;
    }

    /// <summary>
    /// Gets the Bicep identifier for the Azure resource.
    /// </summary>
    /// <param name="resource">The Azure resource.</param>
    /// <returns>A valid Bicep identifier.</returns>
    [AspireExport(Description = "Gets the normalized Bicep identifier for an Azure resource")]
    public static string GetBicepIdentifier(this IAzureResource resource) =>
        Infrastructure.NormalizeBicepIdentifier(resource.Name);

    /// <summary>
    /// Clears all default role assignments for the specified Azure resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The configured <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method removes all default role assignments from the Azure resource. This can be useful when 
    /// role assignments can't be created, for example on existing resources where you don't have permission
    /// to create the assignments.
    /// </remarks>
    /// <example>
    /// Clear default role assignments for an Azure Key Vault resource:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var keyVault = builder.AddAzureKeyVault("keyvault")
    ///     .RunAsExisting("kv-dev-secrets", "rg-keyvault")
    ///     .ClearDefaultRoleAssignments();
    ///
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithReference(keyVault);
    /// </code>
    /// </example>
    [AspireExport(Description = "Clears the default Azure role assignments from a resource")]
    public static IResourceBuilder<T> ClearDefaultRoleAssignments<T>(this IResourceBuilder<T> builder)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var annotations = builder.Resource.Annotations.OfType<DefaultRoleAssignmentsAnnotation>().ToList();
        foreach (var annotation in annotations)
        {
            builder.Resource.Annotations.Remove(annotation);
        }

        return builder;
    }
}
