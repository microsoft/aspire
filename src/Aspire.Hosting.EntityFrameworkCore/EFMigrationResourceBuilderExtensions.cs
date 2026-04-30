// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // PipelineStepAnnotation is experimental; used to wire migration-bundle pipeline steps.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
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
    /// When enabled, migrations will be applied during AppHost startup.
    /// This only affects local run-mode execution. The migrations resource is not deployed with the app,
    /// so the command has no effect during publish or deployment.
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
        var resourceLoggerService = serviceProvider.GetRequiredService<ResourceLoggerService>();
        var logger = resourceLoggerService.GetLogger(migrationResource);

        try
        {
            var resourceCommandService = serviceProvider.GetRequiredService<ResourceCommandService>();
            var result = await resourceCommandService.ExecuteCommandAsync(
                migrationResource,
                "ef-database-update",
                cancellationToken).ConfigureAwait(false);

            if (!result.Success && !result.Canceled)
            {
                logger.LogError(
                    "EF Core database update on startup failed for resource '{ResourceName}'. {ErrorMessage}",
                    migrationResource.Name,
                    result.Message ?? "");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application is shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EF Core database update on startup failed for resource '{ResourceName}'.", migrationResource.Name);
        }
    }

    /// <summary>
    /// Configures the EF migration resource to generate a migration script during publishing.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="idempotent">
    /// If <see langword="true"/> (the default), generates an idempotent script with
    /// <c>IF NOT EXISTS</c> checks so it can be safely re-run against a database that has already
    /// had some or all of the migrations applied.
    /// </param>
    /// <param name="noTransactions">If <c>true</c>, omits transaction statements from the script.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// During <c>aspire publish</c>, the generated SQL script is written to the publish output directory under
    /// the <c>efmigrations</c> folder. The script is included as a deployment artifact, but it is not executed
    /// automatically during deployment.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> PublishAsMigrationScript(
        this IResourceBuilder<EFMigrationResource> builder, bool idempotent = true, bool noTransactions = false)
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
    /// <param name="targetRuntime">
    /// The target runtime identifier for the bundle (e.g., <c>linux-x64</c>, <c>win-x64</c>).
    /// If <see langword="null"/> and <paramref name="publishContainer"/> is <see langword="true"/>,
    /// defaults to <c>linux-x64</c> so the bundle can run inside a Linux container image. When
    /// <paramref name="publishContainer"/> is <see langword="false"/> the current runtime is used.
    /// </param>
    /// <param name="selfContained">
    /// If <see langword="true"/>, creates a self-contained bundle that includes the .NET runtime.
    /// Never defaulted by <paramref name="publishContainer"/> — user-specified value is always respected.
    /// </param>
    /// <param name="publishContainer">
    /// If <see langword="true"/>, the bundle is published as a container image that applies migrations
    /// at deploy time. The resource becomes a compute resource; each target environment deploys it the
    /// same way it deploys any other container (supplying connection strings from referenced
    /// <see cref="IResourceWithConnectionString"/> dependencies via the standard <c>WithReference</c>
    /// mechanism).
    /// </param>
    /// <param name="baseImage">
    /// Overrides the base container image for the generated <c>Dockerfile</c>. When <see langword="null"/>
    /// (the default), the image is derived from the project's target framework — for example,
    /// <c>mcr.microsoft.com/dotnet/runtime:10.0</c> for a <c>net10.0</c> framework-dependent bundle.
    /// Set this when the default is not suitable, e.g. for preview SDKs or custom base images.
    /// Only meaningful when <paramref name="publishContainer"/> is <see langword="true"/>.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// During <c>aspire publish</c>, the bundle executable is written to the publish output directory
    /// under the <c>efmigrations</c> folder. When <paramref name="publishContainer"/> is
    /// <see langword="true"/>, Aspire also generates a <c>Dockerfile</c> that packages the bundle into
    /// a container image; the container reads the connection string from a
    /// <c>ConnectionStrings__&lt;name&gt;</c> environment variable provided by the referenced database
    /// resource (call <c>.WithReference(db)</c> on the migration builder, or the connection string is
    /// injected automatically for every <see cref="IResourceWithConnectionString"/> that the migration
    /// resource <c>.WaitFor</c>s).
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> PublishAsMigrationBundle(
        this IResourceBuilder<EFMigrationResource> builder, string? targetRuntime = null, bool selfContained = false, bool publishContainer = false, string? baseImage = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.PublishAsMigrationBundle = true;
        builder.Resource.BundleSelfContained = selfContained;
        builder.Resource.PublishBundleContainer = publishContainer;
        builder.Resource.BundleBaseImage = baseImage;

        // When publishing as a container image the bundle most likely will run inside a Linux container,
        // so default the target runtime accordingly.
        builder.Resource.BundleTargetRuntime = targetRuntime ?? (publishContainer ? "linux-x64" : null);

        if (publishContainer)
        {
            // The container image wiring is only meaningful when publishing / deploying. In
            // run mode the user interacts with the migration resource via its tool commands
            // (Update Database, Reset Database, etc.), so materializing a container image
            // locally would be confusing and slow. Skip the wiring in run mode entirely.
            if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
            {
                ConfigureBundleContainer(builder);
            }
        }

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
    [AspireExport("withMigrationsProjectFromPath", MethodName = "withMigrationsProject", Description = "Configures a separate project containing the migrations using a path")]
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

    // Base image repositories used when publishing the migration bundle as a container. The
    // non-chiseled runtime-deps image is used for self-contained bundles because the generated
    // Dockerfile relies on shell expansion of the connection-string environment variable in
    // ENTRYPOINT (chiseled images have no /bin/sh). The standard runtime image is used for
    // framework-dependent bundles so the .NET shared framework is present.
    // The image tag (e.g. "10.0") is resolved at generation time from the project's TFM so the
    // image always matches the runtime the bundle was compiled against.
    private const string MinimumImageTag = "10.0";
    private const string SelfContainedBaseImageRepo = "mcr.microsoft.com/dotnet/runtime-deps";
    private const string FrameworkDependentBaseImageRepo = "mcr.microsoft.com/dotnet/runtime";
    // Suffix appended to the image tag for Windows-based containers (nanoserver is the smallest
    // Windows image that includes cmd.exe for shell-form ENTRYPOINT env-var expansion).
    private const string WindowsImageTagSuffix = "-nanoserver-ltsc2022";
    private const string ConnectionStringEnvVarPrefix = "ConnectionStrings__";

    private static void ConfigureBundleContainer(IResourceBuilder<EFMigrationResource> builder)
    {
        var migrationResource = builder.Resource;

        // Use the pipeline output directory as the Docker build context. The generate step writes
        // the bundle to '<outputDir>/efmigrations/<bundleFile>', and the generated Dockerfile COPYs
        // that same file into the image.
        var buildContext = Path.Combine(ResolvePipelineOutputDirectory(builder), "efmigrations");

        // WithDockerfileFactory requires the resource to already have a container image annotation
        // (so the image name/tag can be established). The EF migration resource normally has none,
        // so seed one with the resource name as the image.
        builder.WithImage(migrationResource.Name);
        builder.WithDockerfileFactory(buildContext, _ => Task.FromResult(GenerateDockerfile(migrationResource)));

        // WithDockerfileFactory replaces any existing PipelineStepAnnotation on the resource with
        // its build/push annotation (via EnsureBuildAndPushPipelineAnnotations' Replace mode). That
        // wipes the migration step factory registered by AddEFMigrationsCore, so re-register it.
        builder.WithPipelineStepFactory(EFResourceBuilderExtensions.CreateMigrationPipelineStep);

        // Once the application model is finalized we know which IResourceWithConnectionString
        // dependencies the user declared via WaitFor. Forward them through the standard environment
        // callback so the compute environment injects ConnectionStrings__<name> for the bundle
        // container the same way it does for any other compute resource.
        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var connectionStringResource = GetSingleWaitedOnConnectionStringResource(migrationResource);
            var envVar = connectionStringResource.ConnectionStringEnvironmentVariable
                ?? ConnectionStringEnvVarPrefix + connectionStringResource.Name;

            migrationResource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
            {
                context.EnvironmentVariables[envVar] = new ConnectionStringReference(connectionStringResource, optional: false);
            }));

            return Task.CompletedTask;
        });
    }

    // Resolves the pipeline output directory the same way PipelineOutputService does, so the
    // Docker build context captured at configuration time matches the path the generate step
    // uses at execution time (via IPipelineOutputService.GetOutputDirectory()).
    private static string ResolvePipelineOutputDirectory(IResourceBuilder<EFMigrationResource> builder)
    {
        var configured = builder.ApplicationBuilder.Configuration["Pipeline:OutputPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(builder.ApplicationBuilder.AppHostDirectory, "aspire-output");
    }

    private static IResourceWithConnectionString GetSingleWaitedOnConnectionStringResource(EFMigrationResource migrationResource)
    {
        // WaitForStart adds an implicit WaitAnnotation for the parent of an IResourceWithParent
        // dependency (see ResourceBuilderExtensions.WaitForStartCore), so waiting on a database
        // resource implicitly adds a wait on its server — both land here. Filter ancestors out
        // so the most-specific (leaf) resource wins when the set is parent-related.
        var candidates = new List<IResourceWithConnectionString>();
        if (migrationResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations))
        {
            foreach (var wait in waitAnnotations)
            {
                if (wait.Resource is IResourceWithConnectionString connectionStringResource
                    && !candidates.Any(c => ReferenceEquals(c, connectionStringResource)))
                {
                    candidates.Add(connectionStringResource);
                }
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot publish migration bundle '{migrationResource.Name}' as a container: add " +
                $"'.WaitFor(<database>)' with a database resource that exposes a connection string.");
        }

        // Drop any candidate that is an ancestor (via IResourceWithParent) of another candidate.
        // The leaf resource's connection string already targets the specific database, so the
        // parent server adds nothing and would otherwise be ambiguous here.
        var leaves = candidates
            .Where(candidate => !candidates.Any(other => !ReferenceEquals(other, candidate) && IsAncestorOf(candidate, other)))
            .ToList();

        if (leaves.Count == 1)
        {
            return leaves[0];
        }

        var unrelated = string.Join(", ", leaves.Select(l => $"'{l.Name}'"));
        throw new InvalidOperationException(
            $"Cannot publish migration bundle '{migrationResource.Name}' as a container: multiple " +
            $"unrelated waited-on resources expose a connection string ({unrelated}). A migration " +
            $"bundle targets exactly one database — only call '.WaitFor' with a single " +
            $"IResourceWithConnectionString, or waited-on resources that share a parent chain.");
    }

    private static bool IsAncestorOf(IResource candidate, IResource descendant)
    {
        // Walk the IResourceWithParent chain upward from `descendant` and return true if we hit
        // `candidate`. Bounded to a reasonable depth to avoid any pathological cycles.
        var current = descendant;
        for (var depth = 0; depth < 16; depth++)
        {
            if (current is not IResourceWithParent withParent)
            {
                return false;
            }

            current = withParent.Parent;
            if (ReferenceEquals(current, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the Docker base image for the migration bundle container. Priority:
    /// <list type="number">
    /// <item>User-specified <see cref="EFMigrationResource.BundleBaseImage"/> (wins outright).</item>
    /// <item>Image tag derived from <see cref="EFMigrationResource.ResolvedFramework"/> (flowed
    /// from the generate step, so it matches what <c>dotnet-ef</c> actually compiled against).</item>
    /// <item>Fallback to <see cref="MinimumImageTag"/> when neither is available.</item>
    /// </list>
    /// The resolved tag is clamped to at least <see cref="MinimumImageTag"/> because the generated
    /// Dockerfile relies on features available in .NET 10+ base images.
    /// </summary>
    internal static string ResolveBaseImage(EFMigrationResource migrationResource)
    {
        // User override — return as-is; the user is in full control.
        if (!string.IsNullOrEmpty(migrationResource.BundleBaseImage))
        {
            return migrationResource.BundleBaseImage;
        }

        // Derive the image tag from the framework the bundle was compiled against.
        var imageTag = MinimumImageTag;
        if (TryExtractVersion(migrationResource.ResolvedFramework, out var derivedTag))
        {
            // Never go below MinimumImageTag — older runtimes aren't guaranteed to be
            // compatible with the generated Dockerfile or the bundle entry-point conventions.
            imageTag = Version.Parse(derivedTag) >= Version.Parse(MinimumImageTag)
                ? derivedTag
                : MinimumImageTag;
        }

        // Windows target runtimes need a Windows-specific image tag variant.
        if (IsWindowsRuntime(migrationResource.BundleTargetRuntime))
        {
            imageTag += WindowsImageTagSuffix;
        }

        var baseImageRepo = migrationResource.BundleSelfContained
            ? SelfContainedBaseImageRepo
            : FrameworkDependentBaseImageRepo;

        return $"{baseImageRepo}:{imageTag}";
    }

    private static bool TryExtractVersion(string? tfm, out string version)
    {
        // TFM is e.g. "net8.0", "net10.0", "net10.0-windows". Strip the "net" prefix and any
        // platform suffix to get the numeric version that the Docker image tag uses.
        version = "";

        if (tfm is null || !tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) || tfm.Length <= 3)
        {
            return false;
        }

        var versionSpan = tfm.AsSpan(3);
        var dashIndex = versionSpan.IndexOf('-');
        if (dashIndex >= 0)
        {
            versionSpan = versionSpan[..dashIndex];
        }

        if (Version.TryParse(versionSpan, out _))
        {
            version = versionSpan.ToString();
            return true;
        }

        return false;
    }

    internal static string GenerateDockerfile(EFMigrationResource migrationResource)
    {
        var primary = GetSingleWaitedOnConnectionStringResource(migrationResource);

        var envVarName = primary.ConnectionStringEnvironmentVariable
            ?? ConnectionStringEnvVarPrefix + primary.Name;

        var baseImage = ResolveBaseImage(migrationResource);
        var bundleFileName = EFResourceBuilderExtensions.GetBundleFileName(migrationResource);
        var isWindows = IsWindowsRuntime(migrationResource.BundleTargetRuntime);

        // The bundle is invoked with `--connection <env-ref>` so the EF Core migration tooling
        // uses the connection string provided by the compute environment at runtime. Shell-form
        // ENTRYPOINT is required so the env var is expanded by the container shell — the bundle
        // itself doesn't read ConnectionStrings__* names; it uses whatever string is passed on
        // the command line, which is why we can't rely on .NET configuration binding here.
        // Linux: COPY --chmod=0755 sets the executable bit inline.
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Auto-generated by Aspire for EF Core migration bundle.");
        sb.Append("FROM ").AppendLine(baseImage);

        if (isWindows)
        {
            sb.AppendLine("WORKDIR C:\\app");
            sb.Append("COPY ").Append(bundleFileName).AppendLine(" C:\\app\\efbundle.exe");
            sb.Append("ENTRYPOINT C:\\app\\efbundle.exe -v --connection \"%")
              .Append(envVarName)
              .AppendLine("%\"");
        }
        else
        {
            sb.AppendLine("WORKDIR /app");
            sb.Append("COPY --chmod=0755 ").Append(bundleFileName).AppendLine(" /app/efbundle");
            sb.Append("ENTRYPOINT /app/efbundle -v --connection \"$")
              .Append(envVarName)
              .AppendLine("\"");
        }

        return sb.ToString();
    }

    private static bool IsWindowsRuntime(string? targetRuntime) =>
        targetRuntime is not null && targetRuntime.StartsWith("win", StringComparison.OrdinalIgnoreCase);
}
