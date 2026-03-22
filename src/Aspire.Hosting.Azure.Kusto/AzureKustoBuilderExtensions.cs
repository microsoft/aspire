// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Kusto;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Kusto.Data.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding Kusto resources to the application model.
/// </summary>
public static class AzureKustoBuilderExtensions
{
    /// <summary>
    /// Adds an Azure Data Explorer (Kusto) cluster resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// When adding an <see cref="AzureKustoClusterResource"/> to your application model the resource can then
    /// be referenced by other resources using the resource name. When the dependent resource is using
    /// the extension method <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// then the dependent resource will wait until the Kusto database is available.
    /// </para>
    /// <para>
    /// By default references to the Azure Data Explorer database resources will be assigned the following roles:
    ///
    /// - <see cref="KustoDatabasePrincipalRole.User"/>
    /// </para>
    /// </remarks>
    [AspireExport("addAzureKustoCluster", Description = "Adds an Azure Data Explorer (Kusto) cluster resource")]
    public static IResourceBuilder<AzureKustoClusterResource> AddAzureKustoCluster(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.AddAzureProvisioning();

        var configureInfrastructure = (Aspire.Hosting.Azure.AzureResourceInfrastructure infrastructure) =>
        {
            var cluster = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
                (identifier, name) =>
                {
                    var resource = KustoCluster.FromExisting(identifier);
                    resource.Name = name;
                    return resource;
                },
                (infrastructure) => new KustoCluster(infrastructure.AspireResource.GetBicepIdentifier())
                {
                    // Basic cluster configuration - can be enhanced in the future
                    Sku = new KustoSku()
                    {
                        Name = KustoSkuName.StandardE2aV4,
                        Tier = KustoSkuTier.Standard,
                        Capacity = 2
                    },
                    Tags = { { "aspire-resource-name", infrastructure.AspireResource.Name } }
                });

            // Add cluster URI output for connection strings
            infrastructure.Add(new ProvisioningOutput("clusterUri", typeof(string)) { Value = cluster.ClusterUri.ToBicepExpression() });

            // We need to output name to externalize role assignments.
            infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = cluster.Name.ToBicepExpression() });

            var azureResource = (AzureKustoClusterResource)infrastructure.AspireResource;

            foreach (var database in azureResource.Databases)
            {
                var cdkDatabase = database.ToProvisioningEntity();
                cdkDatabase.Parent = cluster;
                infrastructure.Add(cdkDatabase);
            }
        };

        var resource = new AzureKustoClusterResource(name, configureInfrastructure);
        var resourceBuilder = builder.AddResource(resource);

        AddKustoHealthChecksAndLifecycleManagement(resourceBuilder);
        AddKustoCustomCommands(resourceBuilder);

        return resourceBuilder
            .WithAnnotation(new DefaultRoleAssignmentsAnnotation(new HashSet<RoleDefinition>()));
    }

    /// <summary>
    /// Adds a Kusto read-write database to the application model.
    /// </summary>
    /// <param name="builder">The Kusto server resource builder.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="databaseName">The name of the database. If not provided, this defaults to the same value as <paramref name="name"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("addReadWriteDatabase", Description = "Adds a Kusto read-write database resource")]
    public static IResourceBuilder<AzureKustoReadWriteDatabaseResource> AddReadWriteDatabase(this IResourceBuilder<AzureKustoClusterResource> builder, [ResourceName] string name, string? databaseName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Use the resource name as the database name if it's not provided
        databaseName ??= name;

        var kustoDatabase = new AzureKustoReadWriteDatabaseResource(name, databaseName, builder.Resource);
        builder.Resource.Databases.Add(kustoDatabase);
        var resourceBuilder = builder.ApplicationBuilder.AddResource(kustoDatabase);

        // Register a health check that will be used to verify database is available
        KustoConnectionStringBuilder? kcsb = null;
        resourceBuilder.OnConnectionStringAvailable(async (db, evt, ct) =>
        {
            var connectionString = await db.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false) ??
            throw new DistributedApplicationException($"ConnectionStringAvailableEvent published for resource '{db.Name}', but the connection string was null.");

            kcsb = GetConnectionStringBuilder(builder.Resource, connectionString);
        });

        var healthCheckKey = $"{kustoDatabase.Name}_check";
        resourceBuilder.ApplicationBuilder
            .Services
            .AddHealthChecks()
            .AddAzureKustoHealthCheck(healthCheckKey, isCluster: false, _ => kcsb!);

        resourceBuilder
            .WithHealthCheck(healthCheckKey);

        return resourceBuilder;
    }

    /// <summary>
    /// Configures the Kusto resource to run as an emulator using the Kustainer container.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="AzureKustoEmulatorContainerImageTags.Tag"/> tag of the <inheritdoc cref="AzureKustoEmulatorContainerImageTags.Registry"/>/<inheritdoc cref="AzureKustoEmulatorContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The resource builder to configure.</param>
    /// <param name="configureContainer">
    /// Optional action to configure the Kusto emulator container.
    /// </param>
    /// <returns>The resource builder.</returns>
    [AspireExport("runAsEmulator", Description = "Configures the Kusto cluster to run using the local emulator", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<AzureKustoClusterResource> RunAsEmulator(
        this IResourceBuilder<AzureKustoClusterResource> builder,
        Action<IResourceBuilder<AzureKustoEmulatorResource>>? configureContainer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        // Mark this resource as an emulator for consistent resource identification and tooling support
        builder.WithAnnotation(new EmulatorResourceAnnotation());

        // Add HTTP endpoint to the original resource so the connection string logic can detect emulator mode
        builder.WithHttpEndpoint(targetPort: AzureKustoEmulatorContainerDefaults.DefaultTargetPort, name: "http");

        var surrogate = new AzureKustoEmulatorResource(builder.Resource);
        var surrogateBuilder = builder.ApplicationBuilder.CreateResourceBuilder(surrogate);

        surrogateBuilder
            .WithAnnotation(new ContainerImageAnnotation
            {
                Registry = AzureKustoEmulatorContainerImageTags.Registry,
                Image = AzureKustoEmulatorContainerImageTags.Image,
                Tag = AzureKustoEmulatorContainerImageTags.Tag
            })
            .WithEnvironment("ACCEPT_EULA", "Y");

        configureContainer?.Invoke(surrogateBuilder);

        return builder;
    }

    /// <summary>
    /// Defines the script used to create the database.
    /// </summary>
    /// <remarks>
    /// This script will only be executed when the Kusto resource is running in emulator mode. In production scenarios, the database creation should be handled as part of the provisioning process.
    /// <inheritdoc cref="AzureKustoReadWriteDatabaseResourceExtensions.GetDatabaseCreationScript(AzureKustoReadWriteDatabaseResource)"/>
    /// </remarks>
    /// <param name="builder">The resource builder to configure.</param>
    /// <param name="script">KQL script to create databases, tables, or data.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport("withCreationScript", Description = "Defines the KQL script used to create the database")]
    public static IResourceBuilder<AzureKustoReadWriteDatabaseResource> WithCreationScript(this IResourceBuilder<AzureKustoReadWriteDatabaseResource> builder, string script)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(script);

        // Store script as an annotation on the resource
        builder.WithAnnotation(new AzureKustoCreateDatabaseScriptAnnotation(script));

        return builder;
    }

    /// <summary>
    /// Configures the host port that the Kusto emulator listens on for HTTP query requests.
    /// </summary>
    /// <param name="builder">Kusto emulator resource builder.</param>
    /// <param name="port">Host port to use.</param>
    /// <returns>An <see cref="IResourceBuilder{T}"/> for the <see cref="AzureKustoEmulatorResource"/>.</returns>
    [AspireExport("withHostPort", Description = "Sets the host port for the Kusto emulator endpoint")]
    public static IResourceBuilder<AzureKustoEmulatorResource> WithHostPort(this IResourceBuilder<AzureKustoEmulatorResource> builder, int port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Adds Kusto-specific health checks and lifecycle management.
    /// </summary>
    private static void AddKustoHealthChecksAndLifecycleManagement(IResourceBuilder<AzureKustoClusterResource> resourceBuilder)
    {
        var resource = resourceBuilder.Resource;

        // Register a health check that will be used to verify Kusto is available
        KustoConnectionStringBuilder? kcsb = null;
        resourceBuilder.OnConnectionStringAvailable(async (resource, evt, ct) =>
        {
            var connectionString = await resource.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false) ??
            throw new DistributedApplicationException($"ConnectionStringAvailableEvent published for resource '{resource.Name}', but the connection string was null.");

            kcsb = GetConnectionStringBuilder(resource, connectionString);
        });

        var healthCheckKey = $"{resource.Name}_check";
        resourceBuilder.ApplicationBuilder
            .Services
            .AddHealthChecks()
            .AddAzureKustoHealthCheck(healthCheckKey, isCluster: true, _ => kcsb!);

        // Execute any setup now that Kusto is ready
        resourceBuilder.OnResourceReady(async (server, evt, ct) =>
        {
            if (kcsb is null)
            {
                throw new DistributedApplicationException($"Connection string for Kusto resource '{server.Name}' is not set.");
            }

            if (!server.IsEmulator)
            {
                // We only support automatic database creation when running as an emulator.
                // When running in Azure, the databases will be created as part of the provisioning process.
                return;
            }

            using var adminProvider = KustoClientFactory.CreateCslAdminProvider(kcsb);
            foreach (var kustoDatabase in server.Databases)
            {
                await CreateDatabaseAsync(adminProvider, kustoDatabase, evt.Services, ct).ConfigureAwait(false);
            }
        });

        resourceBuilder.WithHealthCheck(healthCheckKey);
    }

    private static async Task CreateDatabaseAsync(ICslAdminProvider adminProvider, AzureKustoReadWriteDatabaseResource databaseResource, IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var crp = new ClientRequestProperties()
        {
            ClientRequestId = Guid.NewGuid().ToString(),
        };
        crp.SetParameter(ClientRequestProperties.OptionQueryConsistency, ClientRequestProperties.OptionQueryConsistency_Strong);

        var script = databaseResource.GetDatabaseCreationScript();

        var logger = serviceProvider.GetRequiredService<ResourceLoggerService>().GetLogger(databaseResource);
        var rns = serviceProvider.GetRequiredService<ResourceNotificationService>();

        logger.LogDebug("Creating database '{DatabaseName}'", databaseResource.DatabaseName);

        try
        {
            await AzureKustoEmulatorResiliencePipelines.Default.ExecuteAsync(async ct => await adminProvider.ExecuteControlCommandAsync(databaseResource.DatabaseName, script, crp).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Database '{DatabaseName}' created successfully", databaseResource.DatabaseName);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to create database '{DatabaseName}'", databaseResource.DatabaseName);
            await rns.PublishUpdateAsync(databaseResource, state => state with
            {
                State = KnownResourceStates.FailedToStart
            }).ConfigureAwait(false);
        }
    }

    private static KustoConnectionStringBuilder GetConnectionStringBuilder(AzureKustoClusterResource resource, string connectionString)
    {
        var builder = new KustoConnectionStringBuilder(connectionString);

        if (!resource.IsEmulator)
        {
            // When running against Azure, we use the DefaultAzureCredential to authenticate
            // with the Kusto server.
            builder = builder.WithAadAzureTokenCredentialsAuthentication(AzureCredentialHelper.CreateDefaultAzureCredential());
        }

        return builder;
    }

    private static void AddKustoCustomCommands(IResourceBuilder<AzureKustoClusterResource> resourceBuilder)
    {
        resourceBuilder.WithCommand(
            name: "open-kusto-explorer-desktop",
            displayName: "Open in Kusto Explorer (Desktop)",
            executeCommand: context => OnOpenInKustoExplorerDesktop(resourceBuilder, context),
            commandOptions: new CommandOptions
            {
                UpdateState = UpdateStateDesktop,
                IconName = "DatabaseSearch"
            });

        resourceBuilder.WithCommand(
            name: "open-kusto-explorer-web",
            displayName: "Open in Kusto Explorer (Web)",
            executeCommand: context => OnOpenInKustoExplorerWeb(resourceBuilder, context),
            commandOptions: new CommandOptions
            {
                UpdateState = context => UpdateStateWeb(resourceBuilder, context),
                IconName = "DatabaseSearch"
            });

        static ResourceCommandState UpdateStateDesktop(UpdateCommandStateContext context)
        {
            // The Desktop Kusto.Explorer is only available on Windows, so don't show the command on other platforms.
            if (!OperatingSystem.IsWindows())
            {
                return ResourceCommandState.Hidden;
            }

            return context.ResourceSnapshot.State?.Text == KnownResourceStates.Running ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;
        }

        static ResourceCommandState UpdateStateWeb(IResourceBuilder<AzureKustoClusterResource> resourceBuilder, UpdateCommandStateContext context)
        {
            // The web explorer will only auto-connect to allowed domains, so do not show the command when running as an emulator.
            if (resourceBuilder.Resource.IsEmulator)
            {
                return ResourceCommandState.Hidden;
            }

            return context.ResourceSnapshot.State?.Text == KnownResourceStates.Running ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;
        }

        static async Task<ExecuteCommandResult> OnOpenInKustoExplorerDesktop(IResourceBuilder<AzureKustoClusterResource> resourceBuilder, ExecuteCommandContext context)
        {
            var connectionString = await resourceBuilder
                .Resource
                .ConnectionStringExpression
                .GetValueAsync(context.CancellationToken)
                .ConfigureAwait(false) ??
                throw new DistributedApplicationException($"Connection string for Kusto resource '{resourceBuilder.Resource.Name}' is not set.");

            var launcher = new KustoClientToolLauncher();
            var result = launcher.TryLaunchKustoExplorer(title: "", resourceBuilder.Resource.Name, connectionString, requestText: "");

            return result ? CommandResults.Success() : CommandResults.Failure("Failed to launch Kusto Explorer");
        }

        static async Task<ExecuteCommandResult> OnOpenInKustoExplorerWeb(IResourceBuilder<AzureKustoClusterResource> resourceBuilder, ExecuteCommandContext context)
        {
            var connectionString = await resourceBuilder
                .Resource
                .ConnectionStringExpression
                .GetValueAsync(context.CancellationToken)
                .ConfigureAwait(false) ??
                throw new DistributedApplicationException($"Connection string for Kusto resource '{resourceBuilder.Resource.Name}' is not set.");

            var launcher = new KustoClientToolLauncher();
            var result = launcher.TryLaunchKustoWebExplorer(title: "", resourceBuilder.Resource.Name, connectionString, requestText: "");

            if (!result)
            {
                // If the launcher fails (which may mean we're in a remote session or can't detect a browser),
                // show a notification with a clickable link to the Kusto Web Explorer
                var interactionService = context.ServiceProvider.GetRequiredService<IInteractionService>();
                if (interactionService.IsAvailable)
                {
                    _ = await interactionService.PromptMessageBoxAsync(
                        title: "Kusto Web Explorer",
                        message: $"Could not automatically open Kusto Web Explorer for resource '{resourceBuilder.Resource.Name}'. Click [{connectionString}]({connectionString}) to manually open the Web Explorer.",
                        new MessageBoxInteractionOptions
                        {
                            Intent = MessageIntent.Information,
                            EnableMessageMarkdown = true,
                            PrimaryButtonText = "Dismiss",
                            ShowSecondaryButton = false,
                        },
                        context.CancellationToken).ConfigureAwait(false);
                }
            }

            return CommandResults.Success();
        }
    }
}
