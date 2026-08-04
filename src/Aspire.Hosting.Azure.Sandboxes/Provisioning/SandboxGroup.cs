// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Resources;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class SandboxGroup(string bicepIdentifier, string? resourceVersion = null)
    : ProvisionableResource(bicepIdentifier, "Microsoft.App/sandboxGroups", resourceVersion ?? SandboxesResourceVersions.SandboxGroup)
{
    public BicepValue<ResourceIdentifier> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<ResourceIdentifier>? _id;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    public BicepValue<AzureLocation> Location
    {
        get { Initialize(); return _location!; }
        set { Initialize(); _location!.Assign(value); }
    }
    private BicepValue<AzureLocation>? _location;

    public ManagedServiceIdentity Identity
    {
        get { Initialize(); return _identity!; }
    }
    private ManagedServiceIdentity? _identity;

    public BicepDictionary<string> Tags
    {
        get { Initialize(); return _tags!; }
        set { Initialize(); _tags!.Assign(value); }
    }
    private BicepDictionary<string>? _tags;

    public static SandboxGroup FromExisting(string bicepIdentifier, string? resourceVersion = null)
    {
        var resource = new SandboxGroup(bicepIdentifier, resourceVersion)
        {
            IsExistingResource = true
        };

        return resource;
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isOutput: true);
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _location = DefineProperty<AzureLocation>(nameof(Location), ["location"], isRequired: true);
        _identity = DefineModelProperty<ManagedServiceIdentity>(nameof(Identity), ["identity"]);
        _tags = DefineDictionaryProperty<string>(nameof(Tags), ["tags"]);
    }
}
