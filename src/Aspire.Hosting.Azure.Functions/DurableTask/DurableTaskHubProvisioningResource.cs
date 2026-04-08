// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// A custom provisioning resource for Azure Durable Task Hub.
/// This is a sub-resource of a Durable Task Scheduler.
/// This is used until an official Azure.Provisioning.DurableTask package is available.
/// </summary>
internal sealed class DurableTaskHubProvisioningResource : ProvisionableResource
{
    /// <summary>
    /// Creates a new instance of <see cref="DurableTaskHubProvisioningResource"/>.
    /// </summary>
    /// <param name="bicepIdentifier">The Bicep identifier for this resource.</param>
    /// <param name="resourceVersion">The API version for the resource.</param>
    public DurableTaskHubProvisioningResource(string bicepIdentifier, string? resourceVersion = "2025-11-01")
        : base(bicepIdentifier, new ResourceType("Microsoft.DurableTask/schedulers/taskhubs"), resourceVersion)
    {
    }

    /// <summary>
    /// Gets or sets the name of the Task Hub.
    /// </summary>
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    /// <summary>
    /// Gets or sets the parent Durable Task Scheduler.
    /// </summary>
    public DurableTaskSchedulerProvisioningResource? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }
    private ResourceReference<DurableTaskSchedulerProvisioningResource>? _parent;

    /// <inheritdoc/>
    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(Name), ["name"], isOutput: false, isRequired: true);
        _parent = DefineResource<DurableTaskSchedulerProvisioningResource>(nameof(Parent), ["parent"], isRequired: true);
    }

    /// <summary>
    /// Creates a reference to an existing Durable Task Hub resource.
    /// </summary>
    /// <param name="bicepIdentifier">The Bicep identifier for this resource.</param>
    /// <param name="resourceVersion">The API version for the resource.</param>
    /// <returns>A new instance configured as an existing resource reference.</returns>
    public static DurableTaskHubProvisioningResource FromExisting(string bicepIdentifier, string? resourceVersion = "2025-11-01")
    {
        return new DurableTaskHubProvisioningResource(bicepIdentifier, resourceVersion)
        {
            IsExistingResource = true
        };
    }
}
