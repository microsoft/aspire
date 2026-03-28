// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.DurableTask;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding and configuring Durable Task resources within a distributed application.
/// </summary>
public static class DurableTaskResourceExtensions
{
    /// <summary>
    /// Adds a Durable Task scheduler resource to the distributed application.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The logical name of the scheduler resource.</param>
    /// <returns>An <see cref="IResourceBuilder{TResource}"/> for the scheduler resource.</returns>
    /// <example>
    /// Add a Durable Task scheduler resource:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var scheduler = builder.AddDurableTaskScheduler("scheduler");
    /// </code>
    /// </example>
    public static IResourceBuilder<DurableTaskSchedulerResource> AddDurableTaskScheduler(this IDistributedApplicationBuilder builder, string name)
    {
        var scheduler = new DurableTaskSchedulerResource(name);

        scheduler.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);

        return builder.AddResource(scheduler);
    }

    /// <summary>
    /// Configures the Durable Task scheduler to use an existing scheduler instance referenced by the provided connection string.
    /// No new scheduler resource is provisioned.
    /// </summary>
    /// <param name="builder">The scheduler resource builder.</param>
    /// <param name="connectionString">The connection string referencing the existing Durable Task scheduler instance.</param>
    /// <returns>The same <see cref="IResourceBuilder{DurableTaskSchedulerResource}"/> instance for fluent chaining.</returns>
    /// <remarks>
    /// The existing resource annotation is only applied when the execution context is not in publish mode.
    /// </remarks>
    /// <example>
    /// Use an existing scheduler instead of provisioning a new one:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var scheduler = builder.AddDurableTaskScheduler("scheduler")
    ///     .RunAsExisting("Endpoint=https://example;...;");
    /// </code>
    /// </example>
    public static IResourceBuilder<DurableTaskSchedulerResource> RunAsExisting(this IResourceBuilder<DurableTaskSchedulerResource> builder, string connectionString)
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new DurableTaskSchedulerConnectionStringAnnotation(connectionString));
        }

        return builder;
    }

    /// <summary>
    /// Configures the Durable Task scheduler to use an existing scheduler instance referenced by the provided connection string.
    /// No new scheduler resource is provisioned.
    /// </summary>
    /// <param name="builder">The scheduler resource builder.</param>
    /// <param name="connectionString">The connection string parameter referencing the existing Durable Task scheduler instance.</param>
    /// <returns>The same <see cref="IResourceBuilder{DurableTaskSchedulerResource}"/> instance for fluent chaining.</returns>
    /// <remarks>
    /// The existing resource annotation is only applied when the execution context is not in publish mode.
    /// </remarks>
    /// <example>
    /// Use an existing scheduler where the connection string is supplied via a parameter:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var schedulerConnectionString = builder.AddParameter("schedulerConnectionString");
    ///
    /// var scheduler = builder.AddDurableTaskScheduler("scheduler")
    ///     .RunAsExisting(schedulerConnectionString);
    /// </code>
    /// </example>
    public static IResourceBuilder<DurableTaskSchedulerResource> RunAsExisting(this IResourceBuilder<DurableTaskSchedulerResource> builder, IResourceBuilder<ParameterResource> connectionString)
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new DurableTaskSchedulerConnectionStringAnnotation(connectionString.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Configures the Durable Task scheduler to run using the local emulator (only in non-publish modes).
    /// </summary>
    /// <param name="builder">The resource builder for the scheduler.</param>
    /// <param name="configureContainer">Callback that exposes underlying container used for emulation to allow for customization.</param>
    /// <returns>The same <see cref="IResourceBuilder{DurableTaskSchedulerResource}"/> instance for chaining.</returns>
    /// <example>
    /// Run the scheduler locally using the emulator:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var scheduler = builder.AddDurableTaskScheduler("scheduler")
    ///     .RunAsEmulator();
    /// </code>
    /// </example>
    public static IResourceBuilder<DurableTaskSchedulerResource> RunAsEmulator(this IResourceBuilder<DurableTaskSchedulerResource> builder, Action<IResourceBuilder<DurableTaskSchedulerEmulatorResource>>? configureContainer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Mark this resource as an emulator for consistent resource identification and tooling support
        builder.WithAnnotation(new EmulatorResourceAnnotation());

        builder.WithEndpoint(name: "grpc", targetPort: 8080)
               .WithHttpEndpoint(name: "http", targetPort: 8081)
               .WithHttpEndpoint(name: "dashboard", targetPort: 8082)
               .WithUrlForEndpoint("dashboard", c => c.DisplayText = "Scheduler Dashboard")
               .WithAnnotation(new ContainerImageAnnotation
               {
                   Registry = DurableTaskSchedulerEmulatorContainerImageTags.Registry,
                   Image = DurableTaskSchedulerEmulatorContainerImageTags.Image,
                   Tag = DurableTaskSchedulerEmulatorContainerImageTags.Tag
               });

        var emulatorResource = new DurableTaskSchedulerEmulatorResource(builder.Resource);

        var surrogateBuilder =
            builder
                .ApplicationBuilder
                .CreateResourceBuilder(emulatorResource)
                .WithEnvironment(
                    context =>
                    {
                        ReferenceExpressionBuilder namesBuilder = new();

                        var durableTaskHubNames =
                            builder
                                .ApplicationBuilder
                                .Resources
                                .OfType<DurableTaskHubResource>()
                                .Where(th => th.Parent == builder.Resource)
                                .Select(th => th.TaskHubName)
                                .ToList();

                        for (int i = 0; i < durableTaskHubNames.Count; i++)
                        {
                            if (i > 0)
                            {
                                namesBuilder.AppendLiteral(", ");
                            }

                            namesBuilder.AppendFormatted(durableTaskHubNames[i]);
                        }

                        context.EnvironmentVariables["DTS_TASK_HUB_NAMES"] = namesBuilder.Build();
                    });

        configureContainer?.Invoke(surrogateBuilder);

        return builder;
    }

    /// <summary>
    /// Adds a Durable Task hub resource associated with the specified scheduler.
    /// </summary>
    /// <param name="builder">The scheduler resource builder.</param>
    /// <param name="name">The logical name of the task hub resource.</param>
    /// <returns>An <see cref="IResourceBuilder{TResource}"/> for the task hub resource.</returns>
    /// <example>
    /// Add a task hub under a scheduler:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var scheduler = builder.AddDurableTaskScheduler("scheduler").RunAsEmulator();
    ///
    /// var hub = scheduler.AddTaskHub("hub")
    ///     .WithTaskHubName("MyTaskHub");
    /// </code>
    /// </example>
    public static IResourceBuilder<DurableTaskHubResource> AddTaskHub(this IResourceBuilder<DurableTaskSchedulerResource> builder, string name)
    {
        var hub = new DurableTaskHubResource(name, builder.Resource);

        hub.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);

        var hubBuilder = builder.ApplicationBuilder.AddResource(hub);

        hubBuilder.OnResourceReady(
            async (r, e, ct) =>
            {
                var notifications = e.Services.GetRequiredService<ResourceNotificationService>();

                var url = builder.Resource.IsEmulator
                    ? await ReferenceExpression.Create($"{r.Parent.EmulatorDashboardEndpoint}/subscriptions/default/schedulers/default/taskhubs/{r.TaskHubName}").GetValueAsync(ct).ConfigureAwait(false)
                    : null;

                await notifications.PublishUpdateAsync(r, snapshot => snapshot with
                {
                    State = KnownResourceStates.Running,
                    Urls = url is not null
                        ? [new("dashboard", url, false) { DisplayProperties = new() { DisplayName = "Task Hub Dashboard" } }]
                        : []
                }).ConfigureAwait(false);
            });

        return hubBuilder;
    }

    /// <summary>
    /// Sets the name of the Durable Task hub.
    /// </summary>
    /// <param name="builder">The task hub resource builder.</param>
    /// <param name="taskHubName">The name of the Task Hub.</param>
    /// <returns>The same <see cref="IResourceBuilder{DurableTaskHubResource}"/> instance for fluent chaining.</returns>
    /// <example>
    /// Set the task hub name:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var scheduler = builder.AddDurableTaskScheduler("scheduler").RunAsEmulator();
    /// var hub = scheduler.AddTaskHub("hub").WithTaskHubName("MyTaskHub");
    /// </code>
    /// </example>
    public static IResourceBuilder<DurableTaskHubResource> WithTaskHubName(this IResourceBuilder<DurableTaskHubResource> builder, string taskHubName)
    {
        return builder.WithAnnotation(new DurableTaskHubNameAnnotation(taskHubName));
    }

    /// <summary>
    /// Sets the name of the Durable Task hub using a parameter resource.
    /// </summary>
    /// <param name="builder">The task hub resource builder.</param>
    /// <param name="taskHubName">A parameter resource that resolves to the Task Hub name.</param>
    /// <returns>The same <see cref="IResourceBuilder{DurableTaskHubResource}"/> instance for fluent chaining.</returns>
    /// <example>
    /// Set the task hub name from a parameter:
    /// <code>
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var taskHubName = builder.AddParameter("taskHubName");
    ///
    /// var scheduler = builder.AddDurableTaskScheduler("scheduler").RunAsEmulator();
    /// var hub = scheduler.AddTaskHub("hub").WithTaskHubName(taskHubName);
    /// </code>
    /// </example>
    public static IResourceBuilder<DurableTaskHubResource> WithTaskHubName(this IResourceBuilder<DurableTaskHubResource> builder, IResourceBuilder<ParameterResource> taskHubName)
    {
        return builder.WithAnnotation(new DurableTaskHubNameAnnotation(taskHubName.Resource));
    }
}
