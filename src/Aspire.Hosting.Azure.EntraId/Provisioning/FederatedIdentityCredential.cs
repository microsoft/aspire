// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models a federated identity credential on an Entra application registration.
/// </summary>
/// <remarks>
/// The Microsoft Graph Bicep extension names a child resource as <c>'{applicationUniqueName}/{credentialName}'</c>.
/// See <see href="https://learn.microsoft.com/graph/templates/reference/applications/federatedidentitycredentials"/>.
/// </remarks>
internal sealed class FederatedIdentityCredential(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, EntraIdBicepConstants.FederatedIdentityCredentialResourceType, EntraIdBicepConstants.GraphApiVersion)
{
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    public BicepValue<string> Issuer
    {
        get { Initialize(); return _issuer!; }
        set { Initialize(); _issuer!.Assign(value); }
    }
    private BicepValue<string>? _issuer;

    public BicepValue<string> Subject
    {
        get { Initialize(); return _subject!; }
        set { Initialize(); _subject!.Assign(value); }
    }
    private BicepValue<string>? _subject;

    public BicepList<string> Audiences
    {
        get { Initialize(); return _audiences!; }
        set { Initialize(); _audiences!.Assign(value); }
    }
    private BicepList<string>? _audiences;

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _issuer = DefineProperty<string>(nameof(Issuer), ["issuer"], isRequired: true);
        _subject = DefineProperty<string>(nameof(Subject), ["subject"], isRequired: true);
        _audiences = DefineListProperty<string>(nameof(Audiences), ["audiences"], isRequired: true);
    }
}
