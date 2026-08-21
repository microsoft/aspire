// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Azure environment APIs are experimental.
#pragma warning disable ASPIREAZURERG001 // Owned Azure resource groups are experimental.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for Azure resource groups.
/// </summary>
[Experimental("ASPIREAZURERG001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class AzureResourceGroupExtensions
{
    /// <summary>
    /// Adds an Azure resource group owned by the distributed application.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource and the default physical resource-group name.</param>
    /// <param name="location">The Azure location for the resource group.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addAzureResourceGroup dispatcher export.")]
    public static IResourceBuilder<AzureResourceGroupResource> AddAzureResourceGroup(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string location)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        return AddAzureResourceGroupCore(builder, name, location);
    }

    /// <summary>
    /// Adds an Azure resource group owned by the distributed application.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource and the default physical resource-group name.</param>
    /// <param name="location">The parameter containing the Azure location for the resource group.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addAzureResourceGroup dispatcher export.")]
    public static IResourceBuilder<AzureResourceGroupResource> AddAzureResourceGroup(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<ParameterResource> location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return AddAzureResourceGroupCore(builder, name, location.Resource);
    }

    /// <summary>
    /// Adds an Azure resource group owned by the distributed application.
    /// </summary>
    [AspireExport("addAzureResourceGroup")]
    internal static IResourceBuilder<AzureResourceGroupResource> AddAzureResourceGroupForPolyglot(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object location) =>
        location switch
        {
            string literal => builder.AddAzureResourceGroup(name, literal),
            IResourceBuilder<ParameterResource> parameter => builder.AddAzureResourceGroup(name, parameter),
            _ => throw new ArgumentException("Location must be a string or parameter resource builder.", nameof(location))
        };

    /// <summary>
    /// Configures the physical name of the Azure resource group.
    /// </summary>
    /// <param name="builder">The Azure resource-group builder.</param>
    /// <param name="resourceGroupName">The physical resource-group name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withResourceGroupName dispatcher export.")]
    public static IResourceBuilder<AzureResourceGroupResource> WithResourceGroupName(
        this IResourceBuilder<AzureResourceGroupResource> builder,
        string resourceGroupName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(resourceGroupName);

        ThrowIfResourceGroupAlreadyReferenced(builder.Resource);
        builder.Resource.ResourceGroupName = resourceGroupName;
        return builder;
    }

    /// <summary>
    /// Configures the physical name of the Azure resource group.
    /// </summary>
    /// <param name="builder">The Azure resource-group builder.</param>
    /// <param name="resourceGroupName">The parameter containing the physical resource-group name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withResourceGroupName dispatcher export.")]
    public static IResourceBuilder<AzureResourceGroupResource> WithResourceGroupName(
        this IResourceBuilder<AzureResourceGroupResource> builder,
        IResourceBuilder<ParameterResource> resourceGroupName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resourceGroupName);

        ThrowIfResourceGroupAlreadyReferenced(builder.Resource);
        builder.Resource.ResourceGroupName = resourceGroupName.Resource;
        return builder;
    }

    /// <summary>
    /// Configures the physical name of the Azure resource group.
    /// </summary>
    [AspireExport("withResourceGroupName")]
    internal static IResourceBuilder<AzureResourceGroupResource> WithResourceGroupNameForPolyglot(
        this IResourceBuilder<AzureResourceGroupResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object resourceGroupName) =>
        resourceGroupName switch
        {
            string literal => builder.WithResourceGroupName(literal),
            IResourceBuilder<ParameterResource> parameter => builder.WithResourceGroupName(parameter),
            _ => throw new ArgumentException("Resource-group name must be a string or parameter resource builder.", nameof(resourceGroupName))
        };

    /// <summary>
    /// Configures an Azure Bicep resource to deploy into an Aspire-owned resource group.
    /// </summary>
    /// <typeparam name="T">The Azure Bicep resource type.</typeparam>
    /// <param name="builder">The Azure Bicep resource builder.</param>
    /// <param name="resourceGroup">The owned Azure resource group.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport("withOwnedAzureResourceGroup", MethodName = "withResourceGroup")]
    public static IResourceBuilder<T> WithResourceGroup<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureResourceGroupResource> resourceGroup)
        where T : AzureBicepResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resourceGroup);

        if (!ReferenceEquals(builder.ApplicationBuilder, resourceGroup.ApplicationBuilder))
        {
            throw new ArgumentException(
                $"Azure resource group '{resourceGroup.Resource.Name}' belongs to a different distributed application builder.",
                nameof(resourceGroup));
        }

        if (builder.Resource.Scope is not null ||
            builder.Resource.HasAnnotationOfType<ExistingAzureResourceAnnotation>())
        {
            throw new InvalidOperationException(
                $"Azure resource '{builder.Resource.Name}' already has an explicit scope. Configure either the existing scope or the owned resource group, but not both.");
        }

        builder.Resource.Scope = new AzureBicepResourceScope(resourceGroup.Resource.ResourceGroupName);
        builder.Resource.References.Add(resourceGroup.Resource);
        resourceGroup.Resource.AddDependentResource(builder.Resource);
        var dependencyParameterName = Infrastructure.NormalizeBicepIdentifier(
            $"{resourceGroup.Resource.Name}_resourceGroupDependency");
        builder.Resource.Parameters[dependencyParameterName] = resourceGroup.Resource.NameOutputReference;
        if (!AzureDeclaredLocation.IsSet(builder.Resource))
        {
            AzureDeclaredLocation.Set(builder.Resource, resourceGroup.Resource.LocationOutputReference);
        }
        return builder;
    }

    private static IResourceBuilder<AzureResourceGroupResource> AddAzureResourceGroupCore(
        IDistributedApplicationBuilder builder,
        string name,
        object location)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();
        var resource = location switch
        {
            string literal => new AzureResourceGroupResource(name, literal),
            ParameterResource parameter => new AzureResourceGroupResource(name, parameter),
            _ => throw new ArgumentException($"Location type '{location.GetType()}' is not supported.", nameof(location))
        };
        var azureEnvironment = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        resource.ResourceGroupName = ReferenceExpression.Create($"{azureEnvironment.ResourceGroupName}-{name}");
        return builder.ExecutionContext.IsRunMode
            ? builder.CreateResourceBuilder(resource)
            : builder.AddResource(resource);
    }

    private static void ThrowIfResourceGroupAlreadyReferenced(AzureResourceGroupResource resource)
    {
        if (resource.HasDependentResources)
        {
            throw new InvalidOperationException(
                $"Azure resource group '{resource.Name}' is already referenced by another resource. " +
                $"Configure its physical name with '{nameof(WithResourceGroupName)}' before calling '{nameof(WithResourceGroup)}'.");
        }
    }
}
