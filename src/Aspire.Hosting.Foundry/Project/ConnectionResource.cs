// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// The Microsoft Foundry project connection resource scoped to a project.
/// </summary>
public class AzureCognitiveServicesProjectConnectionResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure, AzureCognitiveServicesProjectResource parent) :
    AzureProvisionableAspireResourceWithParent<CognitiveServicesProjectConnection, AzureCognitiveServicesProjectResource>(name, configureInfrastructure, parent)
{
    internal const string ResourceVersion = "2026-03-01";

    /// <inheritdoc/>
    public override CognitiveServicesProjectConnection FromExisting(string bicepIdentifier)
    {
        return CognitiveServicesProjectConnection.FromExisting(bicepIdentifier, ResourceVersion);
    }

    /// <inheritdoc/>

    public override void SetName(CognitiveServicesProjectConnection provisionableResource, BicepValue<string> name)
    {
        provisionableResource.Name = name;
    }
}

/// <summary>
/// A Foundry project connection resource specifically for Grounding with Bing Search connections.
/// </summary>
/// <remarks>
/// This type is used to distinguish Bing grounding connections from other connection types,
/// ensuring that only connections created by <c>AddBingGroundingConnection</c>
/// can be linked to a <see cref="BingGroundingToolResource"/>.
/// </remarks>
[AspireExport]
public class BingGroundingConnectionResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure, AzureCognitiveServicesProjectResource parent) :
    AzureProvisionableAspireResourceWithParent<CognitiveServicesAccountConnection, AzureCognitiveServicesProjectResource>(name, configureInfrastructure, parent)
{
    /// <inheritdoc/>
    public override CognitiveServicesAccountConnection FromExisting(string bicepIdentifier)
    {
        return CognitiveServicesAccountConnection.FromExisting(bicepIdentifier, AzureCognitiveServicesProjectConnectionResource.ResourceVersion);
    }

    /// <inheritdoc/>
    public override void SetName(CognitiveServicesAccountConnection provisionableResource, BicepValue<string> name)
    {
        provisionableResource.Name = name;
    }
}

/// <summary>
/// The Microsoft Foundry account connection resource.
/// </summary>
public class CognitiveServicesAccountConnection(string bicepIdentifier, string? resourceVersion = null) :
    ProvisionableResource(bicepIdentifier, new("Microsoft.CognitiveServices/accounts/connections"), resourceVersion)
{
    /// <summary>
    /// Gets or sets the friendly name of the connection.
    /// </summary>
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    /// <summary>
    /// Gets or sets the connection properties.
    /// </summary>
    public CognitiveServicesConnectionProperties? Properties
    {
        get { Initialize(); return _properties; }
        set
        {
            Initialize();
            if (value is null)
            {
                _properties = null;
            }
            else
            {
                AssignOrReplace(ref _properties, value);
            }
        }
    }
    private CognitiveServicesConnectionProperties? _properties;

    /// <summary>
    /// Gets or sets the parent Foundry account.
    /// </summary>
    public CognitiveServicesAccount? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }
    private ResourceReference<CognitiveServicesAccount>? _parent;

    /// <summary>
    /// Gets the Id.
    /// </summary>
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    /// <summary>
    /// Gets an existing Microsoft Foundry account connection.
    /// </summary>
    public static CognitiveServicesAccountConnection FromExisting(string bicepIdentifier, string? resourceVersion = null)
    {
        var resource = new CognitiveServicesAccountConnection(bicepIdentifier, resourceVersion)
        {
            IsExistingResource = true
        };

        return resource;
    }

    /// <inheritdoc/>
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _properties = DefineModelProperty<CognitiveServicesConnectionProperties>(nameof(Properties), ["properties"]);
        _parent = DefineResource<CognitiveServicesAccount>(nameof(Parent), ["parent"], isRequired: true);
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
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

/// <summary>
/// Connection properties for a Grounding with Bing Search connection.
/// This overrides the category property of ApiKeyAuthConnectionProperties to
/// "GroundingWithBingSearch", which is not an available enum variant.
/// </summary>
internal class BingGroundingConnectionProperties : ApiKeyAuthConnectionProperties
{
    /// <inheritdoc/>
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        DefineProperty<string>("category", ["category"], defaultValue: "GroundingWithBingSearch");
    }
}
