// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// Represents a Durable Task hub resource. A Task Hub groups durable orchestrations and activities.
/// This resource extends the scheduler connection string with the TaskHub name so that clients can
/// connect to the correct hub.
/// </summary>
/// <param name="name">The logical name of the Task Hub (used as the TaskHub value).</param>
/// <param name="scheduler">The durable task scheduler resource whose connection string is the base for this hub.</param>
[AspireExport(ExposeProperties = true)]
public sealed class DurableTaskHubResource(string name, DurableTaskSchedulerResource scheduler)
    : AzureProvisioningResource(name, ConfigureTaskHubInfrastructure), IResourceWithConnectionString, IResourceWithParent<DurableTaskSchedulerResource>, IResourceWithAzureFunctionsConfig
{
    // The TaskHub doesn't create its own infrastructure - it's created as part of the Scheduler's infrastructure.
    // This callback is a no-op but is required by AzureProvisioningResource.
    private static void ConfigureTaskHubInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        // TaskHubs are created within the parent Scheduler's infrastructure.
        // This method intentionally does nothing.
    }

    /// <summary>
    /// Gets the connection string expression composed of the scheduler connection string and the TaskHub name.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"{Parent.ConnectionStringExpression};TaskHub={TaskHubName}");

    /// <summary>
    /// Gets the parent durable task scheduler resource that provides the base connection string.
    /// </summary>
    public DurableTaskSchedulerResource Parent => scheduler;

    /// <summary>
    /// Gets the name of the Task Hub. If not provided, the logical name of this resource is returned.
    /// </summary>
    public ReferenceExpression TaskHubName => GetTaskHubName();

    /// <summary>
    /// Gets the actual Task Hub name as a string for use in provisioning.
    /// </summary>
    internal string HubName => GetHubNameString();

    private string GetHubNameString()
    {
        if (this.TryGetLastAnnotation<DurableTaskHubNameAnnotation>(out var taskHubNameAnnotation))
        {
            return taskHubNameAnnotation.HubName switch
            {
                string hubName => hubName,
                _ => Name // Default to resource name if parameter is used
            };
        }

        return Name;
    }

    /// <summary>
    /// Converts this resource to an Azure Provisioning entity.
    /// </summary>
    /// <returns>A <see cref="DurableTaskHubProvisioningResource"/> instance.</returns>
    internal DurableTaskHubProvisioningResource ToProvisioningEntity()
    {
        var taskHub = new DurableTaskHubProvisioningResource(Infrastructure.NormalizeBicepIdentifier(Name));
        taskHub.Name = HubName;
        return taskHub;
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a TaskHub with the same identifier already exists
        var existingTaskHub = resources.OfType<DurableTaskHubProvisioningResource>()
            .SingleOrDefault(th => th.BicepIdentifier == bicepIdentifier);

        if (existingTaskHub is not null)
        {
            return existingTaskHub;
        }

        // First, add the parent scheduler as an existing resource
        var parentScheduler = (DurableTaskSchedulerProvisioningResource)Parent.AddAsExistingResource(infra);

        // Create the TaskHub as existing, referencing the parent scheduler
        var taskHub = DurableTaskHubProvisioningResource.FromExisting(bicepIdentifier);
        taskHub.Name = HubName;
        taskHub.Parent = parentScheduler;

        infra.Add(taskHub);
        return taskHub;
    }

    /// <inheritdoc />
    void IResourceWithAzureFunctionsConfig.ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName)
    {
        // Injected to support Azure Functions listener initialization via the DTS storage provider.
        target["DURABLE_TASK_SCHEDULER_CONNECTION_STRING"] = Parent.ConnectionStringExpression;
        target["TASKHUB_NAME"] = TaskHubName;
    }

    private ReferenceExpression GetTaskHubName()
    {
        if (this.TryGetLastAnnotation<DurableTaskHubNameAnnotation>(out var taskHubNameAnnotation))
        {
            return taskHubNameAnnotation.HubName switch
            {
                ParameterResource parameter => ReferenceExpression.Create($"{parameter}"),
                string hubName => ReferenceExpression.Create($"{hubName}"),
                _ => throw new InvalidOperationException($"Unexpected Task Hub name type: {taskHubNameAnnotation.HubName.GetType().Name}")
            };
        }

        return ReferenceExpression.Create($"{Name}");
    }
}
