// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models the service principal created for an Entra application registration.
/// </summary>
internal sealed class MicrosoftGraphServicePrincipal(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, EntraIdBicepConstants.ServicePrincipalResourceType, EntraIdBicepConstants.GraphApiVersion)
{
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    public BicepValue<string> AppId
    {
        get { Initialize(); return _appId!; }
        set { Initialize(); _appId!.Assign(value); }
    }
    private BicepValue<string>? _appId;

    public BicepList<string> Owners
    {
        get { Initialize(); return _owners!; }
        set { Initialize(); _owners!.Assign(value); }
    }
    private BicepList<string>? _owners;

    public BicepList<string> Tags
    {
        get { Initialize(); return _tags!; }
        set { Initialize(); _tags!.Assign(value); }
    }
    private BicepList<string>? _tags;

    /// <summary>
    /// Creates a reference to a service principal that already exists in the tenant, such as the one for Microsoft Graph,
    /// addressed by its application (client) ID.
    /// </summary>
    public static MicrosoftGraphExistingServicePrincipal FromExisting(string bicepIdentifier) => new(bicepIdentifier);

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
        _appId = DefineProperty<string>(nameof(AppId), ["appId"], isRequired: true);
        _tags = DefineListProperty<string>(nameof(Tags), ["tags"]);
        _owners = DefineListProperty<string>(nameof(Owners), ["owners", "relationships"]);
    }
}

/// <summary>
/// References a service principal that already exists in the tenant, addressed by its application (client) ID.
/// </summary>
/// <remarks>
/// Azure.Provisioning leaves only the property named <c>Name</c> settable on an existing resource, and it uses that property
/// to address the resource, so the application ID is exposed as <see cref="Name"/>.
/// </remarks>
internal sealed class MicrosoftGraphExistingServicePrincipal : ProvisionableResource
{
    public MicrosoftGraphExistingServicePrincipal(string bicepIdentifier)
        : base(bicepIdentifier, EntraIdBicepConstants.ServicePrincipalResourceType, EntraIdBicepConstants.GraphApiVersion)
    {
        IsExistingResource = true;
    }

    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets or sets the application (client) ID that addresses the service principal.</summary>
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
        _name = DefineProperty<string>(nameof(Name), ["appId"], isRequired: true);
    }
}
