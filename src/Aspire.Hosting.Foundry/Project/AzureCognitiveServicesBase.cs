// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// An Aspire wrapper around an Azure.Provisioning.ProvisionableResource.
/// </summary>
public abstract class AzureProvisionableAspireResource<T>(string name, Action<AzureResourceInfrastructure> configureInfrastructure) :
    AzureProvisioningResource(name, configureInfrastructure)
    where T : ProvisionableResource
{
    /// <summary>
    /// Get the underlying provisionable resource of type T for the given AzureProvisioningResource.
    /// </summary>
    public static T? GetProvisionableResource(AzureResourceInfrastructure infra, string bicepIdentifier)
    {
        return infra.GetProvisionableResources()
            .OfType<T>()
            .SingleOrDefault(r => r.BicepIdentifier == bicepIdentifier);
    }

    /// <summary>
    /// Provide the (usually unique) name of the resource
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <inheritdoc/>
    public override T AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var existing = GetProvisionableResource(infra, bicepIdentifier);
        if (existing is not null)
        {
            return existing;
        }
        var created = FromExisting(bicepIdentifier);
        // Try to keep annotation and `created` in sync
        if (!TryApplyExistingResourceAnnotation(this, infra, created))
        {
            SetName(created, this.NameOutputReference.AsProvisioningParameter(infra));
        }
        infra.Add(created);
        return created;
    }

    /// <summary>
    /// Sets the name of the provisionable resource.
    ///
    /// This is needed because not all ProvisionableResource classes have a name
    /// property with a setter, and we can't put a type bound on T to require it.
    /// </summary>
    public abstract void SetName(T provisionableResource, BicepValue<string> name);

    /// <summary>
    /// Gets the Azure.Provisioning resource from an existing Bicep identifier.
    ///
    /// Because static methods can't be abstract, this is an instance method.
    /// </summary>
    public abstract T FromExisting(string bicepIdentifier);
}

/// <summary>
/// An AzureProvisionableAspireResource that also is IResourceWithParent.
/// </summary>
public abstract class AzureProvisionableAspireResourceWithParent<T, P> :
    AzureProvisionableAspireResource<T>, IResourceWithParent<P>
    where T : ProvisionableResource
    where P : AzureProvisioningResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureProvisionableAspireResourceWithParent{T, P}"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configureInfrastructure">Configures the underlying Azure resource using Azure.Provisioning.</param>
    /// <param name="parent">The parent Azure provisioning resource.</param>
    protected AzureProvisionableAspireResourceWithParent(string name, Action<AzureResourceInfrastructure> configureInfrastructure, P parent)
        : base(name, configureInfrastructure)
    {
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    /// <summary>
    /// Gets the parent resource.
    /// </summary>
    public P Parent { get; }
}

/// <summary>
/// Extension methods for <see cref="AzureProvisionableAspireResource{T}"/>.
/// </summary>
public static class AzureProvisionableAspireResourceExtensions
{
    /// <summary>
    /// Configure the underlying Azure ProvisioningResource for situations
    /// where additional customization is needed.
    /// </summary>
    /// <typeparam name="A">The type of the Aspire resource.</typeparam>
    /// <typeparam name="P">The type of the underlying Provisionable resource</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">An callback to configure the Provisionable resource.</param>
    /// <returns>The resource builder.</returns>
    internal static IResourceBuilder<A> WithConfiguration<A, P>(this IResourceBuilder<A> builder, Action<P> configure)
        where A : AzureProvisionableAspireResource<P>
        where P : ProvisionableResource
    {
        builder.ConfigureInfrastructure(infra =>
        {
            var r = AzureProvisionableAspireResource<P>.GetProvisionableResource(infra, builder.Resource.GetBicepIdentifier()) ?? throw new InvalidOperationException($"Provisionable resource for Aspire resource '{builder.Resource.Name}' not found in infrastructure.");
            configure(r);
        });
        return builder;
    }
}
