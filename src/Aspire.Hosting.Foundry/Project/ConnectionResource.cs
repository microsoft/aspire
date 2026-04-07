// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// The Microsoft Foundry project connection resource scoped to a project.
/// </summary>
public class AzureCognitiveServicesProjectConnectionResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure, AzureCognitiveServicesProjectResource parent) :
    AzureProvisionableAspireResourceWithParent<CognitiveServicesProjectConnection, AzureCognitiveServicesProjectResource>(name, configureInfrastructure, parent)
{
    /// <inheritdoc/>
    public override CognitiveServicesProjectConnection FromExisting(string bicepIdentifier)
    {
        return CognitiveServicesProjectConnection.FromExisting(bicepIdentifier);
    }

    /// <inheritdoc/>

    public override void SetName(CognitiveServicesProjectConnection provisionableResource, BicepValue<string> name)
    {
        provisionableResource.Name = name;
    }
}

/// <summary>
/// The connection properties for an Application Insights connection.
///
/// This is overrides the category property of ApiKeyAuthConnectionProperties to
/// "AppInsights", which is (as of 2026-01-06) not an available enum variant.
/// </summary>
internal class AppInsightsConnectionProperties : ApiKeyAuthConnectionProperties
{
    /// <inheritdoc/>
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        DefineProperty<string>("category", ["category"], defaultValue: "AppInsights");
    }
}

/// <summary>
/// The connection properties for an Azure Key Vault connection.
///
/// This is overrides the category property of ApiKeyAuthConnectionProperties to
/// "AzureKeyVault", which is (as of 2026-01-06) not an available enum variant.
/// </summary>
internal class AzureKeyVaultConnectionProperties : ManagedIdentityAuthTypeConnectionProperties
{
    /// <inheritdoc/>
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        DefineProperty<string>("category", ["category"], defaultValue: "AzureKeyVault");
    }
}

/// <summary>
/// The connection properties for an Azure Key Vault connection.
///
/// This is overrides the category property of ApiKeyAuthConnectionProperties to
/// "AzureStorageAccount", which is (as of 2026-01-06) not an available enum variant.
/// </summary>
internal class AzureStorageAccountConnectionProperties : AadAuthTypeConnectionProperties
{
    /// <inheritdoc/>
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        DefineProperty<string>("category", ["category"], defaultValue: "AzureStorageAccount");
    }
}
