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

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
        _appId = DefineProperty<string>(nameof(AppId), ["appId"], isRequired: true);
        _tags = DefineListProperty<string>(nameof(Tags), ["tags"]);
        _owners = DefineListProperty<string>(nameof(Owners), ["owners", "relationships"]);
    }
}
