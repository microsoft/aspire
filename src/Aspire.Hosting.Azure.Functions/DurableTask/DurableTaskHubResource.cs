// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// Represents a Durable Task hub resource. A Task Hub groups durable orchestrations and activities.
/// This resource extends the scheduler connection string with the TaskHub name so that clients can
/// connect to the correct hub.
/// </summary>
/// <param name="name">The logical name of the Task Hub (used as the TaskHub value).</param>
/// <param name="scheduler">The durable task scheduler resource whose connection string is the base for this hub.</param>
public sealed class DurableTaskHubResource(string name, DurableTaskSchedulerResource scheduler)
    : Resource(name), IResourceWithConnectionString, IResourceWithParent<DurableTaskSchedulerResource>
{
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
