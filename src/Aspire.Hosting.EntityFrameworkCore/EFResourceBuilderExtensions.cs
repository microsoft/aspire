// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREDOTNETTOOL

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding EF Core migration management to projects.
/// </summary>
public static class EFResourceBuilderExtensions
{
    private static string GetShortTypeName(string? fullTypeName)
    {
        if (string.IsNullOrEmpty(fullTypeName))
        {
            return string.Empty;
        }
        var lastDotIndex = fullTypeName.LastIndexOf('.');
        return lastDotIndex >= 0 ? fullTypeName[(lastDotIndex + 1)..] : fullTypeName;
    }

    /// <summary>
    /// Adds EF Core migration management for a specific DbContext type identified by name.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="contextTypeName">The fully qualified name of the DbContext type to manage migrations for.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if migrations for this context type have already been added.</exception>
    /// <remarks>
    /// <para>
    /// Multiple calls to this method with different context types are supported, allowing you to manage
    /// migrations for multiple DbContexts in the same project.
    /// </para>
    /// <para>
    /// This overload is useful when the DbContext type is not available at compile time, such as when
    /// using runtime-discovered context types.
    /// </para>
    /// </remarks>
    [AspireExport("addEFMigrationsWithContextType", MethodName = "addEFMigrations", Description = "Adds EF Core migration management for a specific DbContext type identified by name")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        string contextTypeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(contextTypeName);

        return AddEFMigrationsCore(builder, name, contextTypeName, configureToolResource: null);
    }

    /// <summary>
    /// Adds EF Core migration management for a specific DbContext type identified by name.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="contextTypeName">The fully qualified name of the DbContext type to manage migrations for.</param>
    /// <param name="configureToolResource">Optional callback to configure the dotnet-ef tool resource used for migrations.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if migrations for this context type have already been added.</exception>
    /// <remarks>
    /// <para>
    /// Multiple calls to this method with different context types are supported, allowing you to manage
    /// migrations for multiple DbContexts in the same project.
    /// </para>
    /// <para>
    /// This overload is useful when the DbContext type is not available at compile time, such as when
    /// using runtime-discovered context types.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Action<IResourceBuilder<DotnetToolResource>> callbacks are not ATS-compatible.")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        string contextTypeName,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(contextTypeName);

        return AddEFMigrationsCore(builder, name, contextTypeName, configureToolResource);
    }

    /// <summary>
    /// Adds EF Core migration management for auto-detected DbContext types.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddEFMigrationsCore(builder, name, contextTypeName: null, configureToolResource: null);
    }

    /// <summary>
    /// Adds EF Core migration management for auto-detected DbContext types.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="configureToolResource">Optional callback to configure the dotnet-ef tool resource used for migrations.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    [AspireExportIgnore(Reason = "Action<IResourceBuilder<DotnetToolResource>> callbacks are not ATS-compatible.")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddEFMigrationsCore(builder, name, contextTypeName: null, configureToolResource);
    }

    private static IResourceBuilder<EFMigrationResource> AddEFMigrationsCore(
        IResourceBuilder<ProjectResource> builder,
        string name,
        string? contextTypeName,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        // Check for duplicate context types and null/non-null conflicts
        var existingMigrations = builder.ApplicationBuilder.Resources
            .OfType<EFMigrationResource>()
            .Where(r => r.ProjectResource == builder.Resource)
            .ToList();

        if (contextTypeName != null)
        {
            if (existingMigrations.Any(r => r.ContextTypeName == contextTypeName))
            {
                throw new InvalidOperationException(
                    $"The DbContext type '{GetShortTypeName(contextTypeName)}' has already been registered for EF migrations on resource '{builder.Resource.Name}'.");
            }

            if (existingMigrations.Any(r => r.ContextTypeName == null))
            {
                throw new InvalidOperationException(
                    $"Cannot add migrations for a specific DbContext type when auto-detected migrations have already been registered on resource '{builder.Resource.Name}'.");
            }
        }
        else
        {
            if (existingMigrations.Any())
            {
                throw new InvalidOperationException(
                    $"Cannot add auto-detected migrations when migrations for specific DbContext types have already been registered on resource '{builder.Resource.Name}'.");
            }
        }

        var migrationResource = new EFMigrationResource(name, builder.Resource, contextTypeName)
        {
            ConfigureToolResource = configureToolResource
        };

        var innerBuilder = builder.ApplicationBuilder
            .AddResource(migrationResource)
            .WithParentRelationship(builder)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "EFMigration",
                Properties = [],
                State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, KnownResourceStateStyles.Info)
            })
            .WithIconName("Database")
            .WithPipelineStepFactory(CreateMigrationPipelineStep);

        AddEFMigrationCommands(innerBuilder, migrationResource, contextTypeName);

        return innerBuilder;
    }

    private static IEnumerable<PipelineStep> CreateMigrationPipelineStep(PipelineStepFactoryContext context)
    {
        if (context.Resource is not EFMigrationResource migrationResource
            || !migrationResource.PublishAsMigrationScript && !migrationResource.PublishAsMigrationBundle)
        {
            yield break;
        }

        if (migrationResource.PublishAsMigrationScript)
        {
            yield return new PipelineStep
            {
                Name = $"{migrationResource.Name}-generate-migration-script",
                Description = $"Generate EF Core migration SQL script for {migrationResource.Name}",
                Resource = migrationResource,
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                Action = stepContext => ExecutePipelineOperationAsync(
                    stepContext, migrationResource, "migration script",
                    (executor, outputDir) =>
                    {
                        var outputPath = outputDir is not null
                            ? Path.Combine(outputDir, migrationResource.Name + ".sql")
                            : null;
                        return executor.GenerateMigrationScriptAsync(
                            outputPath,
                            migrationResource.ScriptIdempotent,
                            migrationResource.ScriptNoTransactions);
                    })
            };
        }

        if (migrationResource.PublishAsMigrationBundle)
        {
            yield return new PipelineStep
            {
                Name = $"{migrationResource.Name}-generate-migration-bundle",
                Description = $"Generate EF Core migration bundle for {migrationResource.Name}",
                Resource = migrationResource,
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                Action = stepContext => ExecutePipelineOperationAsync(
                    stepContext, migrationResource, "migration bundle",
                    (executor, outputDir) =>
                    {
                        string? outputPath = null;
                        if (outputDir is not null)
                        {
                            var bundleName = migrationResource.Name;
                            bundleName += OperatingSystem.IsWindows() ? ".exe" : "";
                            outputPath = Path.Combine(outputDir, bundleName);
                        }
                        return executor.GenerateMigrationBundleAsync(
                            outputPath,
                            migrationResource.BundleTargetRuntime,
                            migrationResource.BundleSelfContained);
                    })
            };
        }
    }

    private static async Task ExecutePipelineOperationAsync(
        PipelineStepContext stepContext,
        EFMigrationResource migrationResource,
        string operationName,
        Func<EFCoreOperationExecutor, string?, Task<EFOperationResult>> executeOperation)
    {
        var loggerFactory = stepContext.Services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<EFMigrationResource>();
        var pipelineOptions = stepContext.Services.GetRequiredService<IOptions<PipelineOptions>>();

        using var executor = new EFCoreOperationExecutor(
            migrationResource.ProjectResource,
            migrationResource.MigrationsProjectPath,
            migrationResource.ContextTypeName,
            logger,
            stepContext.CancellationToken,
            stepContext.Services,
            migrationResource.ToolResource);

        string? outputDir = null;
        if (!string.IsNullOrEmpty(pipelineOptions.Value.OutputPath))
        {
            outputDir = Path.Combine(pipelineOptions.Value.OutputPath, "efmigrations");
            Directory.CreateDirectory(outputDir);
        }

        logger.LogInformation("Generating {Operation} for '{ResourceName}'...", operationName, migrationResource.Name);
        var result = await executeOperation(executor, outputDir).ConfigureAwait(false);

        if (result.Success)
        {
            logger.LogInformation("{Operation} generated successfully for '{ResourceName}'.", operationName, migrationResource.Name);
        }
        else
        {
            throw new InvalidOperationException($"Failed to generate {operationName} for '{migrationResource.Name}': {result.ErrorMessage}");
        }
    }

    private const string EFToolPackageId = "dotnet-ef";

    private static void AddEFMigrationCommands(
        IResourceBuilder<EFMigrationResource> migrationBuilder,
        EFMigrationResource migrationResource,
        string? contextTypeName)
    {
        var contextShortName = GetShortTypeName(contextTypeName);

        // Create hidden DotnetToolResource for running EF commands
        var toolName = $"ef-tool-{migrationResource.Name}";
        var startupProjectDir = Path.GetDirectoryName(migrationResource.ProjectResource.GetProjectMetadata().ProjectPath)!;
        var toolBuilder = migrationBuilder.ApplicationBuilder.AddDotnetTool(toolName, EFToolPackageId)
            .WithParentRelationship(migrationBuilder)
            .WithWorkingDirectory(startupProjectDir)
            .WithExplicitStart()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Tool",
                Properties = [],
                IsHidden = true
            });

        migrationResource.ConfigureToolResource?.Invoke(toolBuilder);

        // Copy environment annotations from project resource to tool resource
        if (migrationResource.ProjectResource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var envCallbacks))
        {
            foreach (var callback in envCallbacks)
            {
                toolBuilder.WithAnnotation(callback);
            }
        }

        migrationResource.ToolResource = toolBuilder.Resource;

        migrationBuilder.WithCommand(
            name: "ef-database-update",
            displayName: "Update Database",
            executeCommand: context => ExecuteEFCommandAsync(
                context,
                "Update Database",
                migrationResource,
                executor => executor.UpdateDatabaseAsync()),
            commandOptions: new CommandOptions
            {
                Description = "Apply pending migrations to the database",
                IconName = "ArrowSync",
                IconVariant = IconVariant.Regular,
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-database-drop",
            displayName: "Drop Database",
            executeCommand: context => ExecuteEFCommandAsync(
                context,
                "Drop Database",
                migrationResource,
                executor => executor.DropDatabaseAsync()),
            commandOptions: new CommandOptions
            {
                Description = "Delete the database",
                IconName = "Delete",
                IconVariant = IconVariant.Regular,
                ConfirmationMessage = "Are you sure you want to drop the database? This action cannot be undone.",
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-database-reset",
            displayName: "Reset Database",
            executeCommand: context => ExecuteEFCommandAsync(
                context,
                "Reset Database",
                migrationResource,
                executor => executor.ResetDatabaseAsync()),
            commandOptions: new CommandOptions
            {
                Description = "Drop and recreate the database with all migrations applied",
                IconName = "ArrowReset",
                IconVariant = IconVariant.Regular,
                ConfirmationMessage = "Are you sure you want to reset the database? This will delete all data and cannot be undone.",
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-migrations-add",
            displayName: "Add Migration...",
            executeCommand: context => ExecuteAddMigrationCommandAsync(context, migrationResource),
            commandOptions: new CommandOptions
            {
                Description = "Create a new migration. Note: The target project will need to be recompiled after adding a migration.",
                IconName = "Add",
                IconVariant = IconVariant.Regular,
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-migrations-remove",
            displayName: "Remove Migration",
            executeCommand: context => ExecuteRemoveMigrationCommandAsync(context, migrationResource),
            commandOptions: new CommandOptions
            {
                Description = "Remove the last migration. Note: The target project will need to be recompiled after removing a migration.",
                IconName = "Subtract",
                IconVariant = IconVariant.Regular,
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-database-status",
            displayName: "Get Database Status",
            executeCommand: context => ExecuteGetStatusCommandAsync(context, migrationResource),
            commandOptions: new CommandOptions
            {
                Description = "Show the current migration status of the database",
                IconName = "Info",
                IconVariant = IconVariant.Regular,
                UpdateState = context => GetCommandState(context, migrationResource)
            });
    }

    private static ResourceCommandState GetCommandState(UpdateCommandStateContext _, EFMigrationResource migrationResource)
    {
        if (migrationResource.RequiresRebuild || migrationResource.IsExecutingCommand)
        {
            return ResourceCommandState.Disabled;
        }

        return ResourceCommandState.Enabled;
    }

    private static Task<ExecuteCommandResult> ExecuteEFCommandAsync(
        ExecuteCommandContext context,
        string operationDisplayName,
        EFMigrationResource migrationResource,
        Func<EFCoreOperationExecutor, Task<EFOperationResult>> executeOperation) =>
        ExecuteWithStateManagementAsync(
            context,
            operationDisplayName,
            migrationResource,
            waitForDependencies: true,
            async (executor, logger, _) =>
            {
                var result = await executeOperation(executor).ConfigureAwait(false);

                if (result.Success)
                {
                    logger.LogInformation("EF Core {Operation} command completed successfully.", operationDisplayName);
                    return CommandResults.Success();
                }

                logger.LogError("EF Core {Operation} command failed: {Error}", operationDisplayName, result.ErrorMessage);
                return CommandResults.Failure(result.ErrorMessage);
            });

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only
    /// <summary>
    /// Common wrapper that handles state management and exception handling for EF commands.
    /// </summary>
    private static async Task<ExecuteCommandResult> ExecuteWithStateManagementAsync(
        ExecuteCommandContext context,
        string operationDisplayName,
        EFMigrationResource migrationResource,
        bool waitForDependencies,
        Func<EFCoreOperationExecutor, ILogger, IInteractionService?, Task<ExecuteCommandResult>> executeOperation)
    {
        var resourceLoggerService = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
        var resourceNotificationService = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var interactionService = context.ServiceProvider.GetService<IInteractionService>();
        var logger = resourceLoggerService.GetLogger(migrationResource);

        if (migrationResource.IsExecutingCommand)
        {
            return CommandResults.Failure($"Another command is already running on this resource.");
        }

        migrationResource.IsExecutingCommand = true;

        try
        {
            if (waitForDependencies)
            {
                await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.Waiting, KnownResourceStateStyles.Info).ConfigureAwait(false);
                await resourceNotificationService.WaitForDependenciesAsync(migrationResource, context.CancellationToken).ConfigureAwait(false);
            }

            await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.Running, KnownResourceStateStyles.Info).ConfigureAwait(false);

            logger.LogInformation("Executing EF Core {Operation} command...", operationDisplayName);

            using var executor = new EFCoreOperationExecutor(
                migrationResource.ProjectResource,
                migrationResource.MigrationsProjectPath,
                migrationResource.ContextTypeName,
                logger,
                context.CancellationToken,
                context.ServiceProvider,
                migrationResource.ToolResource);

            var result = await executeOperation(executor, logger, interactionService).ConfigureAwait(false);

            migrationResource.IsExecutingCommand = false;
            if (result.Success)
            {
                await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.Finished, KnownResourceStateStyles.Info).ConfigureAwait(false);
            }
            else
            {
                await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            migrationResource.IsExecutingCommand = false;
            await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.NotStarted, KnownResourceStateStyles.Info).ConfigureAwait(false);
            logger.LogWarning("EF Core {Operation} command was cancelled.", operationDisplayName);
            return CommandResults.Canceled();
        }
        catch (Exception ex)
        {
            migrationResource.IsExecutingCommand = false;
            await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error).ConfigureAwait(false);
            logger.LogError(ex, "EF Core {Operation} command failed with exception.", operationDisplayName);
            return CommandResults.Failure(ex);
        }
    }

    private static Task UpdateStateAsync(
        ResourceNotificationService resourceNotificationService,
        EFMigrationResource migrationResource,
        string state,
        string style) =>
        resourceNotificationService.PublishUpdateAsync(migrationResource, s => s with
        {
            State = new ResourceStateSnapshot(state, style)
        });

    private static Task<ExecuteCommandResult> ExecuteAddMigrationCommandAsync(
        ExecuteCommandContext context,
        EFMigrationResource migrationResource) =>
        ExecuteWithStateManagementAsync(
            context,
            "Add Migration",
            migrationResource,
            waitForDependencies: false,
            async (executor, logger, interaction) =>
            {
                string migrationName;
                if (interaction == null || !interaction.IsAvailable)
                {
                    migrationName = $"Migration_{DateTime.UtcNow:yyyyMMddHHmmss}";
                }
                else
                {
                    var inputResult = await interaction.PromptInputAsync(
                        title: "Add Migration",
                        message: "Enter the name for the new migration.",
                        inputLabel: "Migration Name",
                        placeHolder: "e.g. InitialCreate",
                        cancellationToken: context.CancellationToken).ConfigureAwait(false);

                    if (inputResult.Canceled || string.IsNullOrWhiteSpace(inputResult.Data?.Value))
                    {
                        // Throwing OperationCanceledException lets the wrapper handle state update
                        throw new OperationCanceledException();
                    }

                    migrationName = inputResult.Data.Value;
                }

                var result = await executor.AddMigrationAsync(
                    migrationName,
                    migrationResource.MigrationOutputDirectory,
                    migrationResource.MigrationNamespace).ConfigureAwait(false);

                if (result.Success)
                {
                    logger.LogInformation("Migration '{MigrationName}' created successfully.", migrationName);

                    migrationResource.RequiresRebuild = true;

                    if (interaction != null && interaction.IsAvailable)
                    {
                        await interaction.PromptNotificationAsync(
                            title: "Migration Created",
                            message: $"Migration '{migrationName}' was added successfully.\n\nThe target project needs to be recompiled before the migration can be applied.",
                            options: new NotificationInteractionOptions
                            {
                                Intent = MessageIntent.Warning,
                                ShowSecondaryButton = false
                            },
                            cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogWarning("Migration '{MigrationName}' was added successfully. The target project needs to be recompiled before the migration can be applied.", migrationName);
                    }

                    return CommandResults.Success();
                }

                logger.LogError("Add Migration command failed: {Error}", result.ErrorMessage);
                return CommandResults.Failure(result.ErrorMessage);
            });

    private static Task<ExecuteCommandResult> ExecuteRemoveMigrationCommandAsync(
        ExecuteCommandContext context,
        EFMigrationResource migrationResource) =>
        ExecuteWithStateManagementAsync(
            context,
            "Remove Migration",
            migrationResource,
            waitForDependencies: false,
            async (executor, logger, interactionService) =>
            {
                var result = await executor.RemoveMigrationAsync().ConfigureAwait(false);

                if (result.Success)
                {
                    logger.LogInformation("Migration removed successfully.");

                    migrationResource.RequiresRebuild = true;

                    if (interactionService != null && interactionService.IsAvailable)
                    {
                        await interactionService.PromptNotificationAsync(
                            title: "Migration Removed",
                            message: "The last migration was removed successfully.\n\nThe target project needs to be recompiled.",
                            options: new NotificationInteractionOptions
                            {
                                Intent = MessageIntent.Warning,
                                ShowSecondaryButton = false
                            },
                            cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogWarning("The last migration was removed successfully. The target project needs to be recompiled.");
                    }

                    return CommandResults.Success();
                }

                logger.LogError("Remove Migration command failed: {Error}", result.ErrorMessage);
                return CommandResults.Failure(result.ErrorMessage);
            });

    private static Task<ExecuteCommandResult> ExecuteGetStatusCommandAsync(
        ExecuteCommandContext context,
        EFMigrationResource migrationResource) =>
        ExecuteWithStateManagementAsync(
            context,
            "Get Database Status",
            migrationResource,
            waitForDependencies: false,
            async (executor, logger, interactionService) =>
            {
                var result = await executor.GetDatabaseStatusAsync().ConfigureAwait(false);

                if (result.Success)
                {
                    if (interactionService != null && interactionService.IsAvailable)
                    {
                        await interactionService.PromptMessageBoxAsync(
                            title: "Database Migration Status",
                            message: result.Output ?? "No migration information available.",
                            options: new MessageBoxInteractionOptions
                            {
                                Intent = MessageIntent.Information,
                                ShowSecondaryButton = false,
                                EnableMessageMarkdown = true
                            },
                            cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogInformation("Database status:\n{Status}", result.Output);
                    }

                    return CommandResults.Success();
                }

                logger.LogError("Get Database Status command failed: {Error}", result.ErrorMessage);
                return CommandResults.Failure(result.ErrorMessage);
            });
#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only
}
