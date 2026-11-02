// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models a delegated permission grant (<c>oauth2PermissionGrant</c>) that consents to scopes of an API on behalf of users.
/// </summary>
internal sealed class MicrosoftGraphOAuth2PermissionGrant(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, EntraIdBicepConstants.OAuth2PermissionGrantResourceType, EntraIdBicepConstants.GraphApiVersion)
{
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets or sets the object ID of the client's service principal.</summary>
    public BicepValue<string> ClientId
    {
        get { Initialize(); return _clientId!; }
        set { Initialize(); _clientId!.Assign(value); }
    }
    private BicepValue<string>? _clientId;

    /// <summary>Gets or sets who consents: <c>AllPrincipals</c> grants the scopes tenant-wide.</summary>
    public BicepValue<string> ConsentType
    {
        get { Initialize(); return _consentType!; }
        set { Initialize(); _consentType!.Assign(value); }
    }
    private BicepValue<string>? _consentType;

    /// <summary>Gets or sets the object ID of the API's service principal.</summary>
    public BicepValue<string> ResourceId
    {
        get { Initialize(); return _resourceId!; }
        set { Initialize(); _resourceId!.Assign(value); }
    }
    private BicepValue<string>? _resourceId;

    /// <summary>Gets or sets the granted scope values, separated by a single space.</summary>
    public BicepValue<string> Scope
    {
        get { Initialize(); return _scope!; }
        set { Initialize(); _scope!.Assign(value); }
    }
    private BicepValue<string>? _scope;

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
        _clientId = DefineProperty<string>(nameof(ClientId), ["clientId"], isRequired: true);
        _consentType = DefineProperty<string>(nameof(ConsentType), ["consentType"], isRequired: true);
        _resourceId = DefineProperty<string>(nameof(ResourceId), ["resourceId"], isRequired: true);
        _scope = DefineProperty<string>(nameof(Scope), ["scope"]);
    }
}

/// <summary>
/// Models an app role assignment (<c>appRoleAssignedTo</c>) that grants an application permission of an API to a client service principal.
/// </summary>
internal sealed class MicrosoftGraphAppRoleAssignedTo(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, EntraIdBicepConstants.AppRoleAssignedToResourceType, EntraIdBicepConstants.GraphApiVersion)
{
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets or sets the ID of the app role, defined by the API's service principal, to assign.</summary>
    public BicepValue<string> AppRoleId
    {
        get { Initialize(); return _appRoleId!; }
        set { Initialize(); _appRoleId!.Assign(value); }
    }
    private BicepValue<string>? _appRoleId;

    /// <summary>Gets or sets the object ID of the client's service principal, which receives the role.</summary>
    public BicepValue<string> PrincipalId
    {
        get { Initialize(); return _principalId!; }
        set { Initialize(); _principalId!.Assign(value); }
    }
    private BicepValue<string>? _principalId;

    /// <summary>Gets or sets the object ID of the API's service principal, which defines the role.</summary>
    public BicepValue<string> ResourceId
    {
        get { Initialize(); return _resourceId!; }
        set { Initialize(); _resourceId!.Assign(value); }
    }
    private BicepValue<string>? _resourceId;

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
        _appRoleId = DefineProperty<string>(nameof(AppRoleId), ["appRoleId"], isRequired: true);
        _principalId = DefineProperty<string>(nameof(PrincipalId), ["principalId"], isRequired: true);
        _resourceId = DefineProperty<string>(nameof(ResourceId), ["resourceId"], isRequired: true);
    }
}
