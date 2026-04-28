// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding connection string resources to an application.
/// </summary>
public static class ConnectionStringBuilderExtensions
{
    /// <summary>
    /// Adds a connection string resource to the distributed application with the specified expression.
    /// </summary>
    /// <param name="builder">Distributed application builder</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="connectionStringExpression">The connection string expression.</param>
    /// <returns>An <see cref="IResourceBuilder{ConnectionStringResource}"/> instance.</returns>
    /// <remarks>
    /// This method also enables appending custom data to the connection string based on other resources that expose connection strings.
    /// <example>
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var apiKey = builder.AddParameter("apiKey", secret: true);
    ///
    /// var cs = builder.AddConnectionString("cs", ReferenceExpression.Create($"Endpoint=http://something;Key={apiKey}"));
    ///
    /// var backend = builder
    ///     .AddProject&lt;Projects.Backend&gt;("backend")
    ///     .WithReference(cs)
    ///     .WaitFor(database);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addConnectionString dispatcher export.")]
    public static IResourceBuilder<ConnectionStringResource> AddConnectionString(this IDistributedApplicationBuilder builder, [ResourceName] string name, ReferenceExpression connectionStringExpression)
    {
        var cs = new ConnectionStringResource(name, connectionStringExpression);

        var rb = builder.AddResource(cs);

        // Wait for any referenced resources in the connection string.
        // We only look at top level resources with the assumption that they are transitive themselves.
        var tasks = new List<Task>();
        var resourceNames = new HashSet<string>(StringComparers.ResourceName);

        foreach (var value in cs.ConnectionStringExpression.ValueProviders)
        {
            if (value is IResourceWithoutLifetime)
            {
                // We cannot wait for resources without a lifetime.
                continue;
            }

            if (value is IResource resource)
            {
                if (resourceNames.Add(resource.Name))
                {
                    // Wait for the resource.
                    rb.WaitForStart(builder.CreateResourceBuilder(resource));
                }
            }
            else if (value is IValueWithReferences valueWithReferences)
            {
                foreach (var innerRef in valueWithReferences.References.OfType<IResource>())
                {
                    if (resourceNames.Add(innerRef.Name))
                    {
                        // Wait for the inner resource.
                        rb.WaitForStart(builder.CreateResourceBuilder(innerRef));
                    }
                }
            }
        }

        return rb.WithReferenceRelationship(connectionStringExpression)
                 .WithInitialState(new CustomResourceSnapshot
                 {
                     ResourceType = KnownResourceTypes.ConnectionString,
                     State = KnownResourceStates.NotStarted,
                     Properties = []
                 })
                 .OnInitializeResource(async (r, @evt, ct) =>
                 {
                     try
                     {
                         // This is where waiting happens
                         await @evt.Eventing.PublishAsync(new BeforeResourceStartedEvent(r, @evt.Services), ct).ConfigureAwait(false);

                         // Publish the update with the connection string value and the state as running.
                         // This will allow health checks to start running.
                         await evt.Notifications.PublishUpdateAsync(r, s => s with
                         {
                             State = KnownResourceStates.Running
                         }).ConfigureAwait(false);

                         // Publish the connection string available event for other resources that may depend on this resource.
                         await evt.Eventing.PublishAsync(new ConnectionStringAvailableEvent(r, evt.Services), ct)
                                         .ConfigureAwait(false);
                     }
                     catch (Exception ex)
                     {
                         evt.Logger.LogError(ex, "Failed to resolve connection string for resource '{ResourceName}'", r.Name);

                         // If we fail to resolve the connection string, we set the state to failed.
                         await evt.Notifications.PublishUpdateAsync(r, s => s with
                         {
                             State = KnownResourceStates.FailedToStart
                         }).ConfigureAwait(false);
                     }
                 });
    }

    /// <summary>
    /// Adds a connection string resource to the distributed application with the specified expression.
    /// </summary>
    /// <remarks>
    /// This method also enables appending custom data to the connection string based on other resources that expose connection strings.
    /// </remarks>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="connectionStringBuilder">The callback to configure the connection string expression.</param>
    /// <returns>An <see cref="IResourceBuilder{ConnectionStringResource}"/> instance.</returns>
    /// <example>
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var apiKey = builder.AddParameter("apiKey", secret: true);
    ///
    /// var cs = builder.AddConnectionString("cs", b => b.Append($"Endpoint=http://something;Key={apiKey}"));
    ///
    /// var backend = builder
    ///     .AddProject&lt;Projects.Backend&gt;("backend")
    ///     .WithReference(cs)
    ///     .WaitFor(database);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot app hosts should build a ReferenceExpression explicitly and use the canonical addConnectionString export.")]
    public static IResourceBuilder<ConnectionStringResource> AddConnectionString(this IDistributedApplicationBuilder builder, [ResourceName] string name, Action<ReferenceExpressionBuilder> connectionStringBuilder)
    {
        var rb = new ReferenceExpressionBuilder();
        connectionStringBuilder(rb);
        return builder.AddConnectionString(name, rb.Build());
    }

}
