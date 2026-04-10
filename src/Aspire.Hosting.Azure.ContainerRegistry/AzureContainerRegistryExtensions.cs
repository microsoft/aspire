// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Globalization;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Expressions;
using NCrontab;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Azure Container Registry resources to the application model.
/// </summary>
public static class AzureContainerRegistryExtensions
{
    /// <summary>
    /// Adds an Azure Container Registry resource to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureContainerRegistryResource}"/> builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or empty.</exception>
    [AspireExport(Description = "Adds an Azure Container Registry resource to the distributed application model.")]
    public static IResourceBuilder<AzureContainerRegistryResource> AddAzureContainerRegistry(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var configureInfrastructure = (AzureResourceInfrastructure infrastructure) =>
        {
            var registry = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = ContainerRegistryService.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) => new ContainerRegistryService(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    Sku = new() { Name = ContainerRegistrySkuName.Basic },
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                });

            infrastructure.Add(registry);

            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = registry.Name.ToBicepExpression() });
            infrastructure.Add(new ProvisioningOutput("loginServer", typeof(string)) { Value = registry.LoginServer.ToBicepExpression() });
        };

        var resource = new AzureContainerRegistryResource(name, configureInfrastructure);

        IResourceBuilder<AzureContainerRegistryResource> resourceBuilder;

        // Don't add the resource to the infrastructure if we're in run mode.
        if (builder.ExecutionContext.IsRunMode)
        {
            resourceBuilder = builder.CreateResourceBuilder(resource);
        }
        else
        {
            resourceBuilder = builder.AddResource(resource)
                .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
        }

        SubscribeToAddRegistryTargetAnnotations(builder, resource);

        return resourceBuilder;
    }

    /// <summary>
    /// Subscribes to BeforeStartEvent to add RegistryTargetAnnotation to all resources in the model.
    /// </summary>
    private static void SubscribeToAddRegistryTargetAnnotations(IDistributedApplicationBuilder builder, AzureContainerRegistryResource registry)
    {
        builder.OnBeforeStart((beforeStartEvent, cancellationToken) =>
        {
            foreach (var resource in beforeStartEvent.Model.Resources)
            {
                // Add a RegistryTargetAnnotation to indicate this registry is available as a default target
                resource.Annotations.Add(new RegistryTargetAnnotation(registry));
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Configures a resource that implements <see cref="IContainerRegistry"/> to use the specified Azure Container Registry.
    /// </summary>
    /// <typeparam name="T">The resource type that implements <see cref="IContainerRegistry"/>.</typeparam>
    /// <param name="builder">The resource builder for a resource that implements <see cref="IContainerRegistry"/>.</param>
    /// <param name="registryBuilder">The resource builder for the <see cref="AzureContainerRegistryResource"/> to use.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="registryBuilder"/> is null.</exception>
    [AspireExport("withContainerRegistryAzureContainerRegistry", MethodName = "withAzureContainerRegistry", Description = "Configures a compute environment resource to use an Azure Container Registry.")]
    public static IResourceBuilder<T> WithAzureContainerRegistry<T>(this IResourceBuilder<T> builder, IResourceBuilder<AzureContainerRegistryResource> registryBuilder)
        where T : IResource, IComputeEnvironmentResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registryBuilder);

        // Add a ContainerRegistryReferenceAnnotation to indicate that the resource is using a specific registry
        builder.WithAnnotation(new ContainerRegistryReferenceAnnotation(registryBuilder.Resource));

        return builder;
    }

    /// <summary>
    /// Gets the <see cref="AzureContainerRegistryResource"/> associated with the specified Azure compute environment resource.
    /// </summary>
    /// <typeparam name="T">The resource type that implements <see cref="IAzureComputeEnvironmentResource"/>.</typeparam>
    /// <param name="builder">The resource builder for the compute environment resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureContainerRegistryResource}"/> for the associated registry.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the resource does not have an associated Azure Container Registry,
    /// or when the associated container registry is not an <see cref="AzureContainerRegistryResource"/>.</exception>
    [AspireExport(Description = "Gets the Azure Container Registry associated with a compute environment resource.")]
    public static IResourceBuilder<AzureContainerRegistryResource> GetAzureContainerRegistry<T>(this IResourceBuilder<T> builder)
        where T : IResource, IAzureComputeEnvironmentResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var containerRegistry = builder.Resource.ContainerRegistry ?? throw new InvalidOperationException($"The resource '{builder.Resource.Name}' does not have an associated Azure Container Registry.");
        var registry = containerRegistry as AzureContainerRegistryResource ?? throw new InvalidOperationException($"The Container Registry associated with resource '{builder.Resource.Name}' is not an Azure Container Registry.");

        return builder.ApplicationBuilder.CreateResourceBuilder(registry);
    }

    /// <summary>
    /// Adds a scheduled ACR purge task to remove old or unused container images from the registry.
    /// </summary>
    /// <param name="builder">The resource builder for the <see cref="AzureContainerRegistryResource"/>.</param>
    /// <param name="schedule">The cron schedule for the purge task timer trigger. Must be a five-part cron expression
    /// (<c>minute hour day-of-month month day-of-week</c>); seconds are not supported.</param>
    /// <param name="filter">An optional filter for the <c>acr purge --filter</c> parameter. Only repositories matching this
    /// filter will be purged. Defaults to <c>".*:.*"</c> (all repositories and tags) when <see langword="null"/>.</param>
    /// <param name="ago">The age threshold for <c>acr purge --ago</c>. Images older than this duration will be considered
    /// for removal. Uses Go-style duration format (e.g., <c>2d3h6m</c>). Defaults to <c>0d</c> when <see langword="null"/>.</param>
    /// <param name="keep">The number of most recent images to keep per repository, regardless of age.
    /// Must be greater than zero. Defaults to 3.</param>
    /// <param name="taskName">An optional name for the ACR task resource. If not provided, a name is auto-generated
    /// based on existing purge tasks to avoid conflicts. If a task with the specified name already exists,
    /// an <see cref="ArgumentException"/> is thrown.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureContainerRegistryResource}"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="schedule"/> is <see langword="null"/>, empty, or whitespace,
    /// or when <paramref name="taskName"/> conflicts with an existing task.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="keep"/> is less than 1,
    /// or <paramref name="ago"/> is negative or less than 1 minute (when non-zero).</exception>
    /// <example>
    /// This example configures a daily purge task that removes images older than 7 days, keeping the 5 most recent:
    /// <code>
    /// var acr = builder.AddAzureContainerRegistry("myregistry")
    ///     .WithPurgeTask("0 1 * * *", ago: TimeSpan.FromDays(7), keep: 5);
    /// </code>
    /// </example>
    [AspireExport(Description = "Configures a purge task for the Azure Container Registry resource.")]
    public static IResourceBuilder<AzureContainerRegistryResource> WithPurgeTask(
        this IResourceBuilder<AzureContainerRegistryResource> builder,
        string schedule,
        string? filter = null,
        TimeSpan? ago = null,
        int keep = 3,
        string? taskName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule);
        schedule = schedule.Trim();

        try
        {
            _ = CrontabSchedule.Parse(schedule, new CrontabSchedule.ParseOptions { IncludingSeconds = false });
        }
        catch (CrontabException ex)
        {
            throw new ArgumentException(
                $"The schedule '{schedule}' is not a valid five-part cron expression (minute hour day-of-month month day-of-week). {ex.Message}",
                nameof(schedule),
                ex);
        }

        if (keep < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), keep, "Keep must be greater than zero.");
        }

        var purgeAgo = FormatAgo(ago ?? TimeSpan.Zero);

        return builder.ConfigureInfrastructure(infra =>
        {
            var prefix = "purgeOldImages";

            var registry = infra.GetProvisionableResources().OfType<ContainerRegistryService>().Single();
            var allTasks = infra.GetProvisionableResources().OfType<ContainerRegistryTask>()
                .Where(t => t.Parent == registry)
                .ToList();
            var autoNamedTasks = allTasks
                .Where(t => t.Name.Value?.StartsWith(prefix, StringComparison.Ordinal) == true)
                .ToList();
            var taskIndex = autoNamedTasks.Count;

            if (!string.IsNullOrWhiteSpace(taskName))
            {
                if (allTasks.Any(t => string.Equals(t.Name.Value, taskName, StringComparison.Ordinal)))
                {
                    throw new ArgumentException($"A purge task with the name '{taskName}' already exists.", nameof(taskName));
                }
            }
            else
            {
                taskName = taskIndex == 0 ? prefix : $"{prefix}_{taskIndex}";
            }

            var bicepIdentifier = $"{prefix}_{taskIndex}";

            var purgeTaskCmdVariable = new ProvisioningVariable($"purgeTaskCmd_{taskIndex}", typeof(string))
            {
                Value = CreatePurgeTaskContent(filter, purgeAgo, keep)
            };
            infra.Add(purgeTaskCmdVariable);

            var purgeTask = new ContainerRegistryTask(bicepIdentifier)
            {
                Name = taskName,
                Parent = registry,
                Platform = { OS = ContainerRegistryOS.Linux },
                Step = new ContainerRegistryEncodedTaskStep
                {
                    EncodedTaskContent = new FunctionCallExpression(new IdentifierExpression("base64"), [new IdentifierExpression(purgeTaskCmdVariable.BicepIdentifier)])
                },
                Trigger =
                {
                    TimerTriggers =
                    [
                        new ContainerRegistryTimerTrigger
                        {
                            Name = $"{taskName}_trigger",
                            Schedule = schedule
                        }
                    ]
                }
            };
            infra.Add(purgeTask);
        });
    }

    /// <summary>
    /// Adds role assignments to the specified Azure Container Registry resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource being configured.</typeparam>
    /// <param name="builder">The resource builder for the resource that will have role assignments.</param>
    /// <param name="target">The target Azure Container Registry resource.</param>
    /// <param name="roles">The roles to assign to the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This overload is not available in polyglot app hosts. Use
    /// <see cref="WithRoleAssignments{T}(IResourceBuilder{T}, IResourceBuilder{AzureContainerRegistryResource}, AzureContainerRegistryRole[])"/>
    /// instead.
    /// </remarks>
    [AspireExportIgnore(Reason = "ContainerRegistryBuiltInRole is an Azure.Provisioning type not compatible with ATS. Use the AzureContainerRegistryRole-based overload instead.")]
    public static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureContainerRegistryResource> target,
        params ContainerRegistryBuiltInRole[] roles)
        where T : IResource
    {
        return builder.WithRoleAssignments(target, ContainerRegistryBuiltInRole.GetBuiltInRoleName, roles);
    }

    /// <summary>
    /// Adds role assignments to the specified Azure Container Registry resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource being configured.</typeparam>
    /// <param name="builder">The resource builder for the resource that will have role assignments.</param>
    /// <param name="target">The target Azure Container Registry resource.</param>
    /// <param name="roles">The Azure Container Registry roles to assign to the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when a role value is not a valid <see cref="AzureContainerRegistryRole"/> value.</exception>
    [AspireExport("withContainerRegistryRoleAssignments", Description = "Assigns Azure Container Registry roles to a resource.")]
    internal static IResourceBuilder<T> WithRoleAssignments<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureContainerRegistryResource> target,
        params AzureContainerRegistryRole[] roles)
        where T : IResource
    {
        if (roles is null || roles.Length == 0)
        {
            return builder.WithRoleAssignments(target, Array.Empty<ContainerRegistryBuiltInRole>());
        }

        var builtInRoles = new ContainerRegistryBuiltInRole[roles.Length];
        for (var i = 0; i < roles.Length; i++)
        {
            builtInRoles[i] = roles[i] switch
            {
                AzureContainerRegistryRole.AcrDelete => ContainerRegistryBuiltInRole.AcrDelete,
                AzureContainerRegistryRole.AcrImageSigner => ContainerRegistryBuiltInRole.AcrImageSigner,
                AzureContainerRegistryRole.AcrPull => ContainerRegistryBuiltInRole.AcrPull,
                AzureContainerRegistryRole.AcrPush => ContainerRegistryBuiltInRole.AcrPush,
                AzureContainerRegistryRole.AcrQuarantineReader => ContainerRegistryBuiltInRole.AcrQuarantineReader,
                AzureContainerRegistryRole.AcrQuarantineWriter => ContainerRegistryBuiltInRole.AcrQuarantineWriter,
                _ => throw new ArgumentException($"'{roles[i]}' is not a valid {nameof(AzureContainerRegistryRole)} value.", nameof(roles))
            };
        }

        return builder.WithRoleAssignments(target, builtInRoles);
    }

    private static string CreatePurgeTaskContent(string? filter, string ago, int keep)
    {
        return $"""
            version: v1.1.0
            steps:
            - cmd: acr purge --filter '{filter ?? ".*:.*"}' --ago {ago} --keep {keep}
            """.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> into a Go-style duration string compatible with <c>acr purge --ago</c>.
    /// Valid units: <c>d</c> (days), <c>h</c> (hours), <c>m</c> (minutes).
    /// </summary>
    /// <remarks>
    /// From the docs: https://learn.microsoft.com/azure/container-registry/container-registry-auto-purge#example-scheduled-purge-of-multiple-repositories-in-a-registry
    ///   A Go-style duration string to indicate a duration beyond which images are deleted. The duration consists of a sequence
    ///   of one or more decimal numbers, each with a unit suffix. Valid time units include "d" for days, "h" for hours, and "m"
    ///   for minutes. For example, --ago 2d3h6m selects all filtered images last modified more than two days, 3 hours, and 6 minutes
    ///   ago, and --ago 1.5h selects images last modified more than 1.5 hours ago.
    /// </remarks>
    private static string FormatAgo(TimeSpan ago)
    {
        if (ago.TotalMinutes < 1 && ago != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ago), ago, "Ago must be at least 1 minute to be compatible with acr purge.");
        }

        if (ago == TimeSpan.Zero)
        {
            return "0d";
        }

        var sb = new StringBuilder();

        if (ago.Days > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{ago.Days}d");
        }

        if (ago.Hours > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{ago.Hours}h");
        }

        if (ago.Minutes > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{ago.Minutes}m");
        }

        return sb.ToString();
    }
}
