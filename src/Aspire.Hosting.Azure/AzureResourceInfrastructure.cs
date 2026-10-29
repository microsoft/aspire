// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Azure.Provisioning.Expressions;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// An Azure Provisioning <see cref="Infrastructure" /> which represents the root Bicep module that is generated for an Azure resource.
/// </summary>
/// <ats-summary>An Azure Provisioning <ats-see cref="!:type:Infrastructure" /> which represents the root Bicep module that is generated for an Azure resource.</ats-summary>
[AspireExport(ExposeProperties = true)]
public sealed class AzureResourceInfrastructure : Infrastructure
{
    internal AzureResourceInfrastructure(AzureProvisioningResource resource, string name) : base(name)
    {
        AspireResource = resource;

        // Always add a default location parameter.
        // azd assumes there will be a location parameter for every module.
        // The Infrastructure location resolver will resolve unset Location properties to this parameter.
        Add(new ProvisioningParameter("location", typeof(string))
        {
            Description = "The location for the resource(s) to be deployed.",
            Value = BicepFunction.GetResourceGroup().Location
        });
    }

    /// <summary>
    /// The Aspire <see cref="AzureProvisioningResource"/> resource that this <see cref="AzureResourceInfrastructure"/> represents.
    /// </summary>
    public AzureProvisioningResource AspireResource { get; }

    /// <summary>
    /// Gets the Bicep extension declarations for this infrastructure, keyed by namespace alias.
    /// </summary>
    /// <remarks>
    /// Each value is an extension reference, such as
    /// <c>br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0</c>.
    /// Inline references keep generated modules independent of a <c>bicepconfig.json</c> file.
    /// This experimental API works around the lack of native extension support in Azure.Provisioning.
    /// See <see href="https://github.com/Azure/azure-sdk-for-net/issues/63848">the Azure SDK feature request</see>.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddAzureInfrastructure("graph", infrastructure =>
    /// {
    ///     infrastructure.BicepExtensions["microsoftGraphV1"] =
    ///         "br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0";
    /// });
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Bicep extension declarations are configured through C# infrastructure callbacks.")]
    [Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public Dictionary<string, string> BicepExtensions { get; } = new(StringComparer.Ordinal);

    internal IEnumerable<ProvisioningParameter> GetParameters() => GetProvisionableResources().OfType<ProvisioningParameter>();
}
