// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.DurableTask;

/// <summary>
/// Represents a Durable Task scheduler resource used in Aspire hosting that provides endpoints
/// and a connection string for Durable Task orchestration scheduling.
/// </summary>
/// <param name="name">The unique resource name.</param>
/// <param name="configureInfrastructure"></param>
public sealed class DurableTaskSchedulerResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IResourceWithEndpoints, IResourceWithConnectionString, IResourceWithAzureFunctionsConfig
{
    internal List<DurableTaskHubResource> Hubs { get; } = [];

    /// <summary>
    /// Gets the "schedulerEndpoint" output reference from the bicep template for the Durable Task scheduler resource.
    /// </summary>
    public BicepOutputReference SchedulerEndpoint => new("schedulerEndpoint", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the expression that resolves to the connection string for the Durable Task scheduler.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => CreateConnectionString();

    internal ReferenceExpression EmulatorDashboardEndpoint => CreateDashboardEndpoint();

    /// <summary>
    /// Gets a value indicating whether the Durable Task scheduler is running using the local
    /// emulator (container) instead of a cloud-hosted service.
    /// </summary>
    public bool IsEmulator => this.IsContainer();

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a scheduler with the same identifier already exists
        var existingScheduler = resources.OfType<DurableTaskSchedulerProvisioningResource>()
            .SingleOrDefault(s => s.BicepIdentifier == bicepIdentifier);

        if (existingScheduler is not null)
        {
            return existingScheduler;
        }

        // Create and add new resource if it doesn't exist
        var scheduler = DurableTaskSchedulerProvisioningResource.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(this, infra, scheduler))
        {
            scheduler.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(scheduler);
        return scheduler;
    }

    private ReferenceExpression CreateConnectionString()
    {
        if (IsEmulator)
        {
            var grpcEndpoint = new EndpointReference(this, "grpc");

            return ReferenceExpression.Create($"Endpoint={grpcEndpoint.Property(EndpointProperty.Scheme)}://{grpcEndpoint.Property(EndpointProperty.Host)}:{grpcEndpoint.Property(EndpointProperty.Port)};Authentication=None");
        }

        if (this.TryGetLastAnnotation<DurableTaskSchedulerConnectionStringAnnotation>(out var connectionStringAnnotation))
        {
            return connectionStringAnnotation.ConnectionString switch
            {
                ParameterResource parameterResource => ReferenceExpression.Create($"{parameterResource}"),
                string value => ReferenceExpression.Create($"{value}"),
                _ => throw new InvalidOperationException($"Unexpected connection string type: {connectionStringAnnotation.ConnectionString.GetType().Name}"),
            };
        }

        // For Azure deployment, use the scheduler endpoint from Bicep output
        return ReferenceExpression.Create($"Endpoint={SchedulerEndpoint};Authentication=DefaultAzure");
    }

    private ReferenceExpression CreateDashboardEndpoint()
    {
        if (IsEmulator)
        {
            var dashboardEndpoint = new EndpointReference(this, "dashboard");

            return ReferenceExpression.Create($"{dashboardEndpoint.Property(EndpointProperty.Url)}");
        }

        throw new InvalidOperationException("Dashboard endpoint is only available when running as an emulator.");
    }

    void IResourceWithAzureFunctionsConfig.ApplyAzureFunctionsConfiguration(IDictionary<string, object> target, string connectionName)
    {
        if (IsEmulator)
        {
            // For emulator, use the full connection string
            target[connectionName] = ConnectionStringExpression;
        }
        else
        {
            // For Azure deployment, use the scheduler endpoint
            target[$"{connectionName}__endpoint"] = SchedulerEndpoint;
        }
    }
}
