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
/// See <see href="https://learn.microsoft.com/graph/templates/bicep/reference/applications/federatedidentitycredentials"/>.
/// </remarks>
/// <param name="bicepIdentifier">The resource's identifier in the Bicep module.</param>
public sealed class MicrosoftGraphFederatedIdentityCredential(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, EntraIdBicepConstants.FederatedIdentityCredentialResourceType, EntraIdBicepConstants.GraphApiVersion)
{
    /// <summary>Gets or sets the child resource name as <c>{applicationUniqueName}/{credentialName}</c>.</summary>
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    /// <summary>Gets or sets the trusted token issuer URL.</summary>
    public BicepValue<string> Issuer
    {
        get { Initialize(); return _issuer!; }
        set { Initialize(); _issuer!.Assign(value); }
    }
    private BicepValue<string>? _issuer;

    /// <summary>Gets or sets the subject claim identifying the trusted workload.</summary>
    public BicepValue<string> Subject
    {
        get { Initialize(); return _subject!; }
        set { Initialize(); _subject!.Assign(value); }
    }
    private BicepValue<string>? _subject;

    /// <summary>Gets or sets the audiences accepted for token exchange.</summary>
    public BicepList<string> Audiences
    {
        get { Initialize(); return _audiences!; }
        set { Initialize(); _audiences!.Assign(value); }
    }
    private BicepList<string>? _audiences;

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _issuer = DefineProperty<string>(nameof(Issuer), ["issuer"], isRequired: true);
        _subject = DefineProperty<string>(nameof(Subject), ["subject"], isRequired: true);
        _audiences = DefineListProperty<string>(nameof(Audiences), ["audiences"], isRequired: true);
    }
}
