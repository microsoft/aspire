// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure resource group owned by the distributed application.
/// </summary>
/// <remarks>
/// Use this resource to place Azure resources in a resource group other than the primary
/// resource group used by the Azure deployment.
/// </remarks>
[Experimental("ASPIREAZURERG001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureResourceGroupResource : AzureProvisioningResource
{
    private readonly HashSet<IResource> _dependentResources = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureResourceGroupResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="location">The Azure location for the resource group.</param>
    public AzureResourceGroupResource(string name, string location)
        : this(name, (object)location)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureResourceGroupResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="location">The parameter containing the Azure location for the resource group.</param>
    public AzureResourceGroupResource(string name, ParameterResource location)
        : this(name, (object)location)
    {
        ArgumentNullException.ThrowIfNull(location);
    }

    private AzureResourceGroupResource(string name, object location)
        : base(name, ConfigureResourceGroupInfrastructure)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(location);

        ResourceGroupName = name;
        Location = location;
        AzureDeclaredLocation.Set(this, location);
        Scope = AzureBicepResourceScope.CreateForSubscription();
    }

    internal override bool IsSubscriptionScopedInfrastructure => true;

    internal object ResourceGroupName { get; set; }

    internal object Location { get; set; }

    internal bool HasDependentResources => _dependentResources.Count > 0;

    internal void AddDependentResource(IResource resource) => _dependentResources.Add(resource);

    /// <summary>
    /// Gets a reference to the provisioned resource-group name.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets a reference to the provisioned resource-group identifier.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets a reference to the provisioned resource-group location.
    /// </summary>
    public BicepOutputReference LocationOutputReference => new("location", this);

    private static void ConfigureResourceGroupInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var resource = (AzureResourceGroupResource)infrastructure.AspireResource;
        var location = ToParameter(resource.Location, infrastructure, KnownParameters.Location);
        var resourceGroupName = ToValue(resource.ResourceGroupName, infrastructure, "resourceGroupName");

        var resourceGroup = new ResourceGroup(resource.GetBicepIdentifier())
        {
            Name = resourceGroupName,
            Location = location
        };
        infrastructure.Add(resourceGroup);

        infrastructure.Add(new ProvisioningOutput("name", typeof(string))
        {
            Value = resourceGroup.Name.ToBicepExpression()
        });
        infrastructure.Add(new ProvisioningOutput("id", typeof(string))
        {
            Value = resourceGroup.Id.ToBicepExpression()
        });
        infrastructure.Add(new ProvisioningOutput("location", typeof(string))
        {
            Value = resourceGroup.Location.ToBicepExpression()
        });
    }

    private static BicepValue<string> ToValue(object value, AzureResourceInfrastructure infrastructure, string parameterName) =>
        value switch
        {
            string literal => literal,
            ParameterResource parameter => parameter.AsProvisioningParameter(infrastructure, parameterName),
            IManifestExpressionProvider expression => expression.AsProvisioningParameter(infrastructure, parameterName),
            _ => throw new NotSupportedException($"Azure resource group value type '{value.GetType()}' is not supported.")
        };

    private static ProvisioningParameter ToParameter(object value, AzureResourceInfrastructure infrastructure, string parameterName)
    {
        var parameter = infrastructure.GetParameters().Single(candidate => candidate.BicepIdentifier == parameterName);
        switch (value)
        {
            case string literal:
                parameter.Value = literal;
                break;
            case ParameterResource parameterResource:
                parameter = parameterResource.AsProvisioningParameter(infrastructure, parameterName);
                break;
            default:
                throw new NotSupportedException($"Azure resource group value type '{value.GetType()}' is not supported.");
        }

        return parameter;
    }
}
