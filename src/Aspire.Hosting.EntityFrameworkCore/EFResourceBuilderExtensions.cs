// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREDOTNETTOOL

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.EntityFrameworkCore;
using Aspire.Hosting.Pipelines;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// Adds EF Core migration management for a specific DbContext type.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="dbContextTypeName">The fully qualified name of the DbContext type to manage migrations for.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if migrations for this context type have already been added.</exception>
    /// <remarks>
    /// Multiple calls to this method with different context types are supported, allowing you to manage
    /// migrations for multiple DbContexts in the same project.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addEFMigrations dispatcher export.")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        string dbContextTypeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(dbContextTypeName);

        return AddEFMigrationsCore(builder, name, dbContextTypeName, configureToolResource: null);
    }

    /// <summary>
    /// Adds EF Core migration management for a specific DbContext type.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="dbContextTypeName">The fully qualified name of the DbContext type to manage migrations for.</param>
    /// <param name="configureToolResource">Optional callback to configure the dotnet-ef tool resource used for migrations.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if migrations for this context type have already been added.</exception>
    /// <remarks>
    /// Multiple calls to this method with different context types are supported, allowing you to manage
    /// migrations for multiple DbContexts in the same project.
    /// </remarks>
    [AspireExportIgnore(Reason = "Action<IResourceBuilder<DotnetToolResource>> callbacks are not ATS-compatible.")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        string dbContextTypeName,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(dbContextTypeName);

        return AddEFMigrationsCore(builder, name, dbContextTypeName, configureToolResource);
    }

    /// <summary>
    /// Adds EF Core migration management for the only DbContext type in the target project.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if migrations have already been added for any DbContext type on this project.</exception>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the internal addEFMigrations dispatcher export.")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddEFMigrationsCore(builder, name, dbContextTypeName: null, configureToolResource: null);
    }

    /// <summary>
    /// Adds EF Core migration management for polyglot app hosts.
    /// </summary>
    [AspireExport("addEFMigrations")]
    internal static IResourceBuilder<EFMigrationResource> AddEFMigrationsForPolyglot(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        string? dbContextTypeName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (dbContextTypeName is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(dbContextTypeName);
        }

        return AddEFMigrationsCore(builder, name, dbContextTypeName, configureToolResource: null);
    }

    /// <summary>
    /// Adds EF Core migration management for the only DbContext type in the target project.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="configureToolResource">Optional callback to configure the dotnet-ef tool resource used for migrations.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if migrations have already been added for any DbContext type on this project.</exception>
    [AspireExportIgnore(Reason = "Action<IResourceBuilder<DotnetToolResource>> callbacks are not ATS-compatible.")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddEFMigrationsCore(builder, name, dbContextTypeName: null, configureToolResource);
    }

    private static IResourceBuilder<EFMigrationResource> AddEFMigrationsCore(
        IResourceBuilder<ProjectResource> builder,
        string name,
        string? dbContextTypeName,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        var existingMigrationResources = builder.ApplicationBuilder.Resources
            .OfType<EFMigrationResource>()
            .Where(r => r.ProjectResource == builder.Resource)
            .ToList();

        if (dbContextTypeName != null)
        {
            if (existingMigrationResources.Any(r => r.DbContextTypeName == dbContextTypeName))
            {
                throw new InvalidOperationException(
                    $"The DbContext type '{GetShortTypeName(dbContextTypeName)}' has already been registered for EF migrations on resource '{builder.Resource.Name}'.");
            }

            if (existingMigrationResources.Any(r => r.DbContextTypeName == null))
            {
                throw new InvalidOperationException(
                    $"Cannot register a specific DbContext type for migrations when they have already been registered without a context type on resource '{builder.Resource.Name}'.");
            }
        }
        else if (existingMigrationResources.Count != 0)
        {
            if (existingMigrationResources.Any(r => r.DbContextTypeName == null))
            {
                throw new InvalidOperationException(
                     $"Cannot register migrations without a context type when they have already been registered without a context type on resource '{builder.Resource.Name}'.");
            }
            
            throw new InvalidOperationException(
                $"Cannot register migrations without a context type when they have already been registered for specific DbContext types on resource '{builder.Resource.Name}'.");
        }

        var migrationResource = new EFMigrationResource(name, builder.Resource, dbContextTypeName)
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

        AddEFMigrationCommands(innerBuilder, migrationResource, dbContextTypeName);

        return innerBuilder;
    }

    internal static IEnumerable<PipelineStep> CreateMigrationPipelineStep(PipelineStepFactoryContext context)
    {
        if (context.PipelineContext.ExecutionContext.IsRunMode
            || context.Resource is not EFMigrationResource migrationResource
            || (!migrationResource.PublishAsMigrationScript && !migrationResource.PublishAsMigrationBundle))
        {
            return [];
        }

        var steps = new List<PipelineStep>();

        var scriptStepName = migrationResource.PublishAsMigrationScript
            ? $"{migrationResource.Name}-generate-migration-script"
            : null;

        var bundleStepName = migrationResource.PublishAsMigrationBundle
            ? $"{migrationResource.Name}-generate-migration-bundle"
            : null;

        // Serialize publish-time generate steps across every EFMigrationResource in the model.
        // Each `dotnet-ef` invocation triggers a `dotnet build` (the bundle step explicitly runs
        // without `--no-build` because the bundle command needs the build to target a specific
        // runtime). Two concurrent `dotnet-ef` runs can race on:
        //   - the shared obj/bin output when two migrations target the same startup project,
        //   - the per-user `dotnet tool exec` cache (NuGet install + extract) used by every
        //     DotnetToolResource regardless of project, and
        //   - the per-user MSBuild node-reuse / NuGet restore caches under %USERPROFILE%.
        // None of those are safe under concurrent `dotnet-ef` invocations, so we chain ALL
        // migration generate steps in the model — not just the ones sharing a startup project.
        //
        // The chain is built by deterministically ordering sibling migrations by name and pointing
        // the first step of each migration at the last step of the previous migration. The
        // graph is therefore: <m1>-script -> <m1>-bundle -> <m2>-script -> <m2>-bundle -> ...
        // which is acyclic (the per-migration script -> bundle edge already exists and the
        // cross-migration edge only flows forward in the deterministic ordering).
        var crossMigrationPredecessor = GetPreviousMigrationLastStepName(context.PipelineContext.Model, migrationResource);

        if (migrationResource.PublishAsMigrationScript)
        {
            List<string> scriptDependsOn = crossMigrationPredecessor is not null ? [crossMigrationPredecessor] : [];

            steps.Add(new PipelineStep
            {
                Name = scriptStepName!,
                Description = $"Generate EF Core migration SQL script for {migrationResource.Name}",
                Resource = migrationResource,
                DependsOnSteps = scriptDependsOn,
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                Action = stepContext => ExecutePublishPipelineOperationAsync(
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
            });
        }

        if (migrationResource.PublishAsMigrationBundle)
        {
            var publishesContainer = migrationResource.PublishBundleContainer
                && context.PipelineContext.ExecutionContext.IsPublishMode;

            List<string> requiredBy = publishesContainer
                ? [WellKnownPipelineSteps.Publish, $"build-{migrationResource.Name}"]
                : [WellKnownPipelineSteps.Publish];

            // Prefer the per-migration script step as the dependency when present (the cross-migration
            // edge is already attached to the script step in that case). Only attach the cross-migration
            // edge directly to the bundle step when this migration produces no script step.
            List<string> bundleDependsOn = [];
            if (scriptStepName is not null)
            {
                bundleDependsOn.Add(scriptStepName);
            }
            else if (crossMigrationPredecessor is not null)
            {
                bundleDependsOn.Add(crossMigrationPredecessor);
            }

            steps.Add(new PipelineStep
            {
                Name = bundleStepName!,
                Description = $"Generate EF Core migration bundle for {migrationResource.Name}",
                Resource = migrationResource,
                DependsOnSteps = bundleDependsOn,
                RequiredBySteps = requiredBy,
                Action = stepContext => ExecutePublishPipelineOperationAsync(
                    stepContext, migrationResource, "migration bundle",
                    (executor, outputDir) =>
                    {
                        var outputPath = outputDir is not null
                            ? Path.Combine(outputDir, GetBundleFileName(migrationResource))
                            : null;
                        return executor.GenerateMigrationBundleAsync(
                            outputPath,
                            migrationResource.BundleTargetRuntime,
                            migrationResource.BundleSelfContained);
                    })
            });
        }

        return steps;
    }

    // Returns the name of the last publish-time step produced by the migration that immediately
    // precedes <paramref name="current"/> in a stable ordering of all migrations in the model.
    // Returns null when <paramref name="current"/> is the first such migration (no predecessor)
    // or the only one.
    private static string? GetPreviousMigrationLastStepName(DistributedApplicationModel model, EFMigrationResource current)
    {
        EFMigrationResource? predecessor = null;
        foreach (var sibling in model.Resources.OfType<EFMigrationResource>())
        {
            if (ReferenceEquals(sibling, current) ||
                (!sibling.PublishAsMigrationScript && !sibling.PublishAsMigrationBundle))
            {
                continue;
            }

            // Stable ordinal ordering by resource name keeps the chain deterministic regardless
            // of model traversal order. Only siblings whose name sorts before this one can
            // possibly act as a predecessor.
            if (StringComparer.Ordinal.Compare(sibling.Name, current.Name) >= 0)
            {
                continue;
            }

            if (predecessor is null || StringComparer.Ordinal.Compare(sibling.Name, predecessor.Name) > 0)
            {
                predecessor = sibling;
            }
        }

        if (predecessor is null)
        {
            return null;
        }

        // The bundle step always follows the script step within the same migration, so it is the
        // last step when present.
        return predecessor.PublishAsMigrationBundle
            ? $"{predecessor.Name}-generate-migration-bundle"
            : $"{predecessor.Name}-generate-migration-script";
    }

    private static async Task ExecutePublishPipelineOperationAsync(
        PipelineStepContext stepContext,
        EFMigrationResource migrationResource,
        string operationName,
        Func<EFCoreOperationExecutor, string?, Task<EFOperationResult>> executeOperation)
    {
        var logger = stepContext.Logger;
#pragma warning disable ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var pipelineOutputService = stepContext.Services.GetRequiredService<IPipelineOutputService>();
#pragma warning restore ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        using var executor = new EFCoreOperationExecutor(
            migrationResource.ProjectResource,
            migrationResource.MigrationsProjectPath,
            migrationResource.DbContextTypeName,
            logger,
            stepContext.CancellationToken,
            stepContext.Services,
            migrationResource.ToolResource);

        var outputDir = Path.Combine(pipelineOutputService.GetOutputDirectory(), "efmigrations");
        Directory.CreateDirectory(outputDir);

        logger.LogInformation("Generating {Operation} for '{ResourceName}'...", operationName, migrationResource.Name);
        var result = await executeOperation(executor, outputDir).ConfigureAwait(false);

        // Flow the resolved target framework back to the resource so the Dockerfile generation
        // step can pick the matching base image tag without re-parsing the project file.
        if (executor.ResolvedFramework is not null && migrationResource.ResolvedFramework is null)
        {
            migrationResource.ResolvedFramework = executor.ResolvedFramework;
        }

        if (result.Success)
        {
            logger.LogInformation("{Operation} generated successfully for '{ResourceName}'.", operationName, migrationResource.Name);
        }
        else
        {
            throw new InvalidOperationException($"Failed to generate {operationName} for '{migrationResource.Name}': {result.ErrorMessage}");
        }
    }

    internal static string GetBundleFileName(EFMigrationResource migrationResource)
    {
        // The bundle is produced for a specific target runtime, so its extension follows that
        // runtime's conventions — not the host OS running `aspire publish`. When no explicit
        // runtime was requested the runtime defaults to the host, so fall back to the OS check.
        var runtime = migrationResource.BundleTargetRuntime;
        var isWindowsBundle = runtime is not null
            ? runtime.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            : OperatingSystem.IsWindows();

        return migrationResource.Name + (isWindowsBundle ? ".exe" : string.Empty);
    }

    private static async Task<ExecuteCommandResult> StartEfToolResourceAsync(ExecuteCommandContext context, DotnetToolResource toolResource)
    {
        var notificationService = context.Services.GetRequiredService<ResourceNotificationService>();
        var resourceStarted = false;
        Process? process = null;

        try
        {
            var executableAnnotation = toolResource.Annotations.OfType<ExecutableAnnotation>().LastOrDefault();
            if (executableAnnotation is null)
            {
                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = $"Executable configuration was not found for EF tool resource '{context.ResourceName}'."
                };
            }

            var executionContext = context.Services.GetService<DistributedApplicationExecutionContext>()
                ?? new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

            var executionConfiguration = await ExecutionConfigurationBuilder.Create(toolResource)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(executionContext, context.Logger, context.CancellationToken).ConfigureAwait(false);

            if (executionConfiguration.Exception is not null)
            {
                await notificationService.PublishUpdateAsync(toolResource, s => s with
                {
                    State = KnownResourceStates.FailedToStart
                }).ConfigureAwait(false);

                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = executionConfiguration.Exception.Message
                };
            }

            var startInfo = new ProcessStartInfo(executableAnnotation.Command)
            {
                WorkingDirectory = executableAnnotation.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Build command-line arguments by directly invoking each annotation's Callback.
            // We intentionally bypass ExecutionConfigurationBuilder.WithArgumentsConfig() here
            // because it uses EvaluateOnceAsync which caches callback results. When the tool
            // resource is reused across sequential EF commands (e.g., script then bundle),
            // the cached BuildToolExecArguments callback does not re-populate the shared
            // callbackContext.Args list, so later annotations (the per-command EF args) run
            // against an empty list.
            if (toolResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var cmdLineAnnotations))
            {
                IList<object> args = [];
                var callbackContext = new CommandLineArgsCallbackContext(args, toolResource, context.CancellationToken)
                {
                    Logger = context.Logger,
                    ExecutionContext = executionContext
                };

                foreach (var ann in cmdLineAnnotations)
                {
                    await ann.Callback(callbackContext).ConfigureAwait(false);
                }

                foreach (var arg in callbackContext.Args)
                {
                    startInfo.ArgumentList.Add(arg.ToString()!);
                }
            }

            foreach (var kvp in executionConfiguration.EnvironmentVariables)
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }

            await notificationService.PublishUpdateAsync(toolResource, s => s with
            {
                State = KnownResourceStates.Starting,
                StartTimeStamp = DateTime.UtcNow,
                StopTimeStamp = null
            }).ConfigureAwait(false);

            resourceStarted = true;

            process = Process.Start(startInfo);
            if (process is null)
            {
                await notificationService.PublishUpdateAsync(toolResource, s => s with
                {
                    State = KnownResourceStates.FailedToStart
                }).ConfigureAwait(false);

                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = $"Failed to start EF tool resource '{context.ResourceName}'."
                };
            }

            await notificationService.PublishUpdateAsync(toolResource, s => s with
            {
                State = KnownResourceStates.Running
            }).ConfigureAwait(false);

            var resourceLoggerService = context.Services.GetRequiredService<ResourceLoggerService>();
            var resourceLogger = resourceLoggerService.GetLogger(toolResource);

            var stderrBuilder = new StringBuilder();
            var stdoutTask = EFCoreOperationExecutor.StreamOutputAsync(
                process.StandardOutput, resourceLogger, isErrorOutput: false, captureBuilder: null, context.CancellationToken);
            var stderrTask = EFCoreOperationExecutor.StreamOutputAsync(
                process.StandardError, resourceLogger, isErrorOutput: true, captureBuilder: stderrBuilder, context.CancellationToken);

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            var finalState = process.ExitCode == 0 ? KnownResourceStates.Finished : KnownResourceStates.FailedToStart;
            await notificationService.PublishUpdateAsync(toolResource, s => s with
            {
                State = finalState,
                StopTimeStamp = DateTime.UtcNow,
                ExitCode = process.ExitCode
            }).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var errorMessage = stderrBuilder.ToString();
                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(errorMessage)
                        ? $"EF tool resource '{context.ResourceName}' exited with code {process.ExitCode}."
                        : errorMessage
                };
            }

            return CommandResults.Success();
        }
        catch (OperationCanceledException)
        {
            if (resourceStarted)
            {
                try
                {
                    if (process is not null && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process has already exited or has not started; continue publishing the terminal state.
                }

                await notificationService.PublishUpdateAsync(toolResource, s => s with
                {
                    State = KnownResourceStates.FailedToStart,
                    StopTimeStamp = DateTime.UtcNow
                }).ConfigureAwait(false);
            }

            return CommandResults.Canceled();
        }
        catch (Exception ex)
        {
            if (resourceStarted)
            {
                try
                {
                    if (process is not null && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process has already exited or has not started; continue publishing the terminal state.
                }

                await notificationService.PublishUpdateAsync(toolResource, s => s with
                {
                    State = KnownResourceStates.FailedToStart,
                    StopTimeStamp = DateTime.UtcNow
                }).ConfigureAwait(false);
            }

            return new ExecuteCommandResult
            {
                Success = false,
                Message = ex.Message
            };
        }
        finally
        {
            process?.Dispose();
        }
    }

    private const string EFToolPackageId = "dotnet-ef";

    private static void AddEFMigrationCommands(
        IResourceBuilder<EFMigrationResource> migrationBuilder,
        EFMigrationResource migrationResource,
        string? dbContextTypeName)
    {
        var contextShortName = GetShortTypeName(dbContextTypeName);

        // Create hidden DotnetToolResource for running EF commands
        var toolName = $"ef-tool-{migrationResource.Name}";
        var startupProjectDir = Path.GetDirectoryName(migrationResource.ProjectResource.GetProjectMetadata().ProjectPath)!;
        var toolBuilder = migrationBuilder.ApplicationBuilder.AddDotnetTool(toolName, EFToolPackageId)
            .WithParentRelationship(migrationBuilder)
            .WithWorkingDirectory(startupProjectDir)
            .WithExplicitStart()
            .WithHidden()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Tool",
                Properties = []
            });

        // Register the EF-specific start command. The tool resource is captured by the closure
        // so it works in both run mode (via resource commands) and publish mode (via pipeline steps).
        var toolResource = toolBuilder.Resource;
        toolBuilder.WithCommand(
            name: EFCoreOperationExecutor.ToolStartCommandName,
            displayName: "Start",
            executeCommand: context => StartEfToolResourceAsync(context, toolResource));

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
                UpdateState = context => GetCommandState(context, migrationResource),
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "name",
                        InputType = InputType.Text,
                        Label = "Migration Name",
                        Required = true,
                        Placeholder = "e.g. InitialCreate"
                    }
                ]
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
        var resourceLoggerService = context.Services.GetRequiredService<ResourceLoggerService>();
        var resourceNotificationService = context.Services.GetRequiredService<ResourceNotificationService>();
        var interactionService = context.Services.GetService<IInteractionService>();
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
                migrationResource.DbContextTypeName,
                logger,
                context.CancellationToken,
                context.Services,
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
                var migrationName = context.Arguments.GetString("name")!;

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
}
