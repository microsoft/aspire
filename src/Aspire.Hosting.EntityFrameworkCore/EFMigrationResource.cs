// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETTOOL

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.EntityFrameworkCore;

/// <summary>
/// Represents an EF Core migration resource associated with a project.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="projectResource">The parent project resource that contains the DbContext.</param>
/// <param name="dbContextTypeName">The fully qualified name of the DbContext type, or null to auto-detect.</param>
/// <remarks>
/// The resource inherits from <see cref="ContainerResource"/> so it can be published as a container image
/// that runs the migration bundle at deploy time when
/// <see cref="EFMigrationResourceBuilderExtensions.PublishAsMigrationBundle(IResourceBuilder{EFMigrationResource}, string?, bool, bool, string?)"/>
/// is called with <c>publishContainer: true</c>.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class EFMigrationResource(string name, ProjectResource projectResource, string? dbContextTypeName)
    : ContainerResource(name)
{
    /// <summary>
    /// Gets the parent project resource that contains the DbContext.
    /// </summary>
    public ProjectResource ProjectResource { get; } = projectResource;

    /// <summary>
    /// Gets the fully qualified name of the DbContext type to use for migrations, or null to auto-detect.
    /// </summary>
    /// <remarks>
    /// This property is used to specify which DbContext to use when the project contains multiple DbContext types.
    /// When null, the EF Core tools will auto-detect the DbContext to use.
    /// </remarks>
    public string? DbContextTypeName { get; } = dbContextTypeName;

    /// <summary>
    /// Gets or sets whether a migration script should be generated during publishing.
    /// </summary>
    public bool PublishAsMigrationScript { get; set; }

    /// <summary>
    /// Gets or sets whether the migration script should be idempotent (include IF NOT EXISTS checks).
    /// </summary>
    public bool ScriptIdempotent { get; set; }

    /// <summary>
    /// Gets or sets whether to omit transaction statements from the migration script.
    /// </summary>
    public bool ScriptNoTransactions { get; set; }

    /// <summary>
    /// Gets or sets whether a migration bundle should be generated during publishing.
    /// </summary>
    public bool PublishAsMigrationBundle { get; set; }

    /// <summary>
    /// Gets or sets the target runtime identifier for the migration bundle (e.g., "linux-x64", "win-x64").
    /// </summary>
    public string? BundleTargetRuntime { get; set; }

    /// <summary>
    /// Gets or sets whether the migration bundle should be self-contained.
    /// </summary>
    public bool BundleSelfContained { get; set; }

    /// <summary>
    /// Gets or sets the base container image for the migration bundle container.
    /// </summary>
    /// <remarks>
    /// When set, this overrides the default base image selection entirely. Use this when the
    /// default image (derived from the project's target framework) isn't suitable — for example
    /// when targeting a preview SDK or a custom base image with extra dependencies.
    /// Example: <c>"mcr.microsoft.com/dotnet/runtime:11.0-preview"</c>.
    /// </remarks>
    public string? BundleBaseImage { get; set; }

    /// <summary>
    /// Gets or sets the target framework resolved from the project during bundle generation.
    /// </summary>
    /// <remarks>
    /// Populated by the generate pipeline step after <c>dotnet-ef</c> resolves the project's
    /// build settings. Used to derive the Docker base image tag so the container runtime matches
    /// the framework the bundle was compiled against. Not intended for user assignment — set
    /// <see cref="BundleBaseImage"/> instead to override the base image.
    /// </remarks>
    internal string? ResolvedFramework { get; set; }

    /// <summary>
    /// Gets or sets whether the migration bundle should be published as a container image that applies
    /// the migrations to the database at deploy time.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the resource is materialized as a <see cref="ContainerResource"/>
    /// carrying a generated <c>Dockerfile</c> that wraps the migration bundle. The compute environment
    /// (Docker Compose, Azure Container Apps, Kubernetes, Azure App Service, Azure Functions, etc.)
    /// deploys it alongside the other compute resources and supplies the connection string from any
    /// <see cref="IResourceWithConnectionString"/> dependency declared via <c>WithReference</c> or
    /// <c>WaitFor</c>.
    /// </remarks>
    public bool PublishBundleContainer { get; set; }

    /// <summary>
    /// Gets or sets the output directory for new migrations. Used by the Add Migration command.
    /// </summary>
    /// <remarks>
    /// If not specified, migrations will be placed in the default 'Migrations' directory.
    /// </remarks>
    public string? MigrationOutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets the namespace for new migrations. Used by the Add Migration command.
    /// </summary>
    /// <remarks>
    /// If not specified, the namespace will be derived from the project's default namespace.
    /// </remarks>
    public string? MigrationNamespace { get; set; }

    /// <summary>
    /// Gets or sets the path to the project containing the migrations, when it's not the same as the startup project.
    /// </summary>
    /// <remarks>
    /// If not specified, migrations are assumed to be in the startup project.
    /// When specified, this project's path will be used as the target for migration operations.
    /// </remarks>
    public string? MigrationsProjectPath { get; set; }

    /// <summary>
    /// Gets or sets the callback to configure the dotnet-ef tool resource.
    /// </summary>
    internal Action<IResourceBuilder<DotnetToolResource>>? ConfigureToolResource { get; set; }

    /// <summary>
    /// Gets or sets the dotnet-ef tool resource used to execute EF commands.
    /// </summary>
    internal DotnetToolResource ToolResource { get; set; } = null!;

    /// <summary>
    /// Gets or sets whether a migration was recently added that requires a project rebuild.
    /// </summary>
    // TODO: Remove this after #14388 is implemented
    internal bool RequiresRebuild { get; set; }

    /// <summary>
    /// Gets or sets whether a command is currently executing on this resource.
    /// </summary>
    internal bool IsExecutingCommand { get; set; }
}
