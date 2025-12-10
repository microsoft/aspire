// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for configuring EF Core migration resources.
/// </summary>
public static class EFMigrationResourceBuilderExtensions
{
    /// <summary>
    /// Configures the EF migration resource to run database update when the AppHost starts.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// When enabled, migrations will be applied during AppHost startup. The resource state will transition
    /// from "Pending" to "Running" to "Finished" (or "FailedToStart" on error).
    /// </para>
    /// <para>
    /// A health check is automatically registered for this resource, allowing other resources to use
    /// <c>.WaitFor()</c> to wait until migrations complete before starting.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> RunDatabaseUpdateOnStart(this IResourceBuilder<EFMigrationResource> builder)
    {
        var migrationResource = builder.Resource;
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, ct) =>
        {
            // Schedule the migration command to run asynchronously after startup completes to avoid deadlocks.
            // See #15234
            var _ = ExecuteMigrationsAsync(@event.Services, migrationResource, ct);
            return Task.CompletedTask;
        });
        return builder;
    }

    private static async Task ExecuteMigrationsAsync(
        IServiceProvider serviceProvider,
        EFMigrationResource migrationResource,
        CancellationToken cancellationToken)
    {
        try
        {
            var resourceCommandService = serviceProvider.GetRequiredService<ResourceCommandService>();
            var result = await resourceCommandService.ExecuteCommandAsync(
                migrationResource,
                "ef-database-update",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application is shutting down
        }
        catch (Exception ex)
        {
            var resourceLoggerService = serviceProvider.GetRequiredService<ResourceLoggerService>();
            var logger = resourceLoggerService.GetLogger(migrationResource);
            logger.LogError(ex, "EF Core database update on startup failed for resource '{ResourceName}'.", migrationResource.Name);
        }
    }

    /// <summary>
    /// Configures the EF migration resource to generate a migration script during publishing.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="idempotent">If <c>true</c>, generates an idempotent script with IF NOT EXISTS checks.</param>
    /// <param name="noTransactions">If <c>true</c>, omits transaction statements from the script.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> PublishAsMigrationScript(this IResourceBuilder<EFMigrationResource> builder, bool idempotent = false, bool noTransactions = false)
    {
        builder.Resource.PublishAsMigrationScript = true;
        builder.Resource.ScriptIdempotent = idempotent;
        builder.Resource.ScriptNoTransactions = noTransactions;
        return builder;
    }

    /// <summary>
    /// Configures the EF migration resource to generate a migration bundle during publishing.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="targetRuntime">The target runtime identifier for the bundle (e.g., "linux-x64", "win-x64"). If null, uses the current runtime.</param>
    /// <param name="selfContained">If <c>true</c>, creates a self-contained bundle that includes the .NET runtime.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> PublishAsMigrationBundle(this IResourceBuilder<EFMigrationResource> builder, string? targetRuntime = null, bool selfContained = false)
    {
        builder.Resource.PublishAsMigrationBundle = true;
        builder.Resource.BundleTargetRuntime = targetRuntime;
        builder.Resource.BundleSelfContained = selfContained;
        return builder;
    }

    /// <summary>
    /// Configures the output directory for new migrations created with the Add Migration command.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="outputDirectory">The output directory path relative to the project root.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// If not specified, migrations will be placed in the default 'Migrations' directory.
    /// Example: "Data/Migrations" or "Infrastructure/Migrations".
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> WithMigrationOutputDirectory(this IResourceBuilder<EFMigrationResource> builder, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputDirectory);
        builder.Resource.MigrationOutputDirectory = outputDirectory;
        return builder;
    }

    /// <summary>
    /// Configures the namespace for new migrations created with the Add Migration command.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="namespace">The namespace for generated migrations.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// If not specified, the namespace will be derived from the project's default namespace.
    /// Example: "MyApp.Data.Migrations" or "MyApp.Infrastructure.Migrations".
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> WithMigrationNamespace(this IResourceBuilder<EFMigrationResource> builder, string @namespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(@namespace);
        builder.Resource.MigrationNamespace = @namespace;
        return builder;
    }

    /// <summary>
    /// Configures a separate project containing the migrations using a project path.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="projectPath">The path to the project file containing the migrations.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Use this method when the migrations are in a different project than the startup project.
    /// The target project's path will be used for migration operations while the startup project
    /// remains the original project.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> WithMigrationsProject(this IResourceBuilder<EFMigrationResource> builder, string projectPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        projectPath = projectPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        projectPath = Path.GetFullPath(Path.Combine(builder.ApplicationBuilder.AppHostDirectory, projectPath));
        builder.Resource.MigrationsProjectPath = projectPath;
        return builder;
    }

    /// <summary>
    /// Configures a separate project containing the migrations using a project metadata type.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <typeparam name="TProject">The project metadata type generated by the Aspire build tooling.</typeparam>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Use this method when the migrations are in a different project than the startup project.
    /// The target project's path will be used for migration operations while the startup project
    /// remains the original project.
    /// </para>
    /// <example>
    /// <code>
    /// var migrations = project.AddEFMigrations&lt;MyDbContext&gt;("migrations")
    ///     .WithMigrationsProject&lt;Projects.MyMigrationsProject&gt;();
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> WithMigrationsProject<TProject>(this IResourceBuilder<EFMigrationResource> builder)
        where TProject : IProjectMetadata, new()
    {
        builder.Resource.MigrationsProjectPath = new TProject().ProjectPath;
        return builder;
    }
}
