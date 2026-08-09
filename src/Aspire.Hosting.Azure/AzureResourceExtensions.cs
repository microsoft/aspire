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
    /// <remarks>
    /// This API only changes the manifest representation; it does not change the resource model used by other publishers.
    /// </remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    [Obsolete("PublishAsConnectionString only works with the manifest publisher and is obsolete. Use AddConnectionString in publish-mode app model code instead.")]
    [AspireExport]
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
    [AspireExport]
    public static string GetBicepIdentifier(this IAzureResource resource) =>
        Infrastructure.NormalizeBicepIdentifier(resource.Name);

    /// <summary>
    /// Clears all default role assignments for the specified Azure resource.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The configured <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
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
