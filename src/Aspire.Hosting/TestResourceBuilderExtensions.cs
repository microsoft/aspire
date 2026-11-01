// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared;
using Aspire.Shared.UserSecrets;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for marking resources as participants in an <c>aspire test</c> run.
/// </summary>
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public static class TestResourceBuilderExtensions
{
    /// <summary>
    /// Configures a resource's participation and result collection in an <c>aspire test</c> run.
    /// </summary>
    /// <ats-summary>Configures a resource's testing participation and result collectors.</ats-summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">An optional synchronous callback that immediately configures testing options.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Configuration runs immediately. Async collectors start alongside resource execution and may
    /// use <see cref="ResourceTestingContext.WaitForCompletionAsync"/> before harvesting final artifacts.
    /// The command waits for all marked resources, including their replicas, and all collectors.
    /// It then shuts down the application unless <c>--keep-alive</c> was requested.
    /// A terminal state with exit code zero passes; startup failure, a nonzero exit code, or an unknown
    /// terminal exit code fails. Resources must publish completion through <see cref="ResourceNotificationService"/>.
    /// The annotation does not change the lifetime of <c>aspire run</c>.
    /// Published failures or collector exceptions fail testing without discarding other results.
    /// Outside testing, configuration still runs but output paths are null and collectors do not run.
    /// </para>
    /// <para>
    /// Calling this method again replaces the collectors, including removing them for a marker-only call,
    /// while preserving the resource's existing run context and output directory. Launch arguments and
    /// environment variables configured by an earlier call therefore remain aligned with collection.
    /// Each logical resource uses a directory named after the resource directly beneath the run directory.
    /// Replicas share that directory; this API does not allocate separate per-replica output paths.
    /// </para>
    /// <example>
    /// <code lang="C#">
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api");
    /// builder.AddExecutable("tests", "test-runner", ".")
    ///     .WithReference(api)
    ///     .WaitFor(api)
    ///     .WithTesting();
    /// </code>
    /// </example>
    /// </remarks>
    /// <ats-remarks>
    /// Waits for every marked resource, including replicas, and all collectors before completion.
    /// The application shuts down unless keep-alive was requested.
    /// Exit code zero passes. Startup failure, a nonzero exit code, or an unknown terminal exit code fails.
    /// This does not change the lifetime of aspire run.
    /// Configuration runs immediately. Collectors start alongside resource execution during testing.
    /// The CLI supplies private resource output destinations; those paths are absent during ordinary runs.
    /// Repeated calls replace collectors while preserving the existing run context and output directory.
    /// </ats-remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    /// <exception cref="ArgumentException">The resource name cannot be used as a portable testing output directory.</exception>
    /// <exception cref="IOException">A new resource's testing output path already exists or cannot be created securely.</exception>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    [Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
    public static IResourceBuilder<T> WithTesting<T>(this IResourceBuilder<T> builder, Action<TestingOptions>? callback = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.TryGetLastAnnotation<TestingAnnotation>(out var annotation);
        var existingOptions = annotation?.Options;
        var runContext = existingOptions?.RunContext;
        var resourceOutputPath = existingOptions?.OutputPath;
        if (existingOptions is null &&
            builder.ApplicationBuilder.ExecutionContext.IsRunMode &&
            builder.ApplicationBuilder.Configuration[KnownConfigNames.TestOutputPath] is { } outputPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            runContext = new TestRunContext(Path.GetFullPath(outputPath));
            // The CLI creates the run directory before model construction. Allocate only within
            // that private boundary so immediate configuration can safely use a real destination.
            SocketPermissionHelper.CreateDirectory(runContext.OutputPath, repairExisting: false);
            resourceOutputPath = CreateResourceOutputDirectory(runContext.OutputPath, builder.Resource.Name);
        }
        var options = new TestingOptions(runContext, builder.Resource.Name, resourceOutputPath);
        callback?.Invoke(options);
        options.CompleteConfiguration();

        return builder.WithAnnotation(new TestingAnnotation(options), ResourceAnnotationMutationBehavior.Replace);
    }

    private static string CreateResourceOutputDirectory(string runOutputPath, string resourceName)
    {
        ModelName.ValidateName("Testing resource", resourceName);
        // Model names allow Windows device names (for example CON), but output paths must
        // be portable. Reject rather than sanitize, which could merge distinct resources.
        if (!AspireSecretsPathHelper.IsValidEnvironmentName(resourceName))
        {
            throw new ArgumentException($"The resource name '{resourceName}' cannot be used as a portable testing output directory.", nameof(resourceName));
        }

        var resourceOutputPath = Path.Combine(runOutputPath, resourceName);
        // Model names exclude separators and dots, including reserved run artifacts such as
        // summary.json. A case-normalized exclusive reservation also protects case-insensitive
        // resource identity on case-sensitive filesystems and concurrent configuration.
        var reservationPath = Path.Combine(runOutputPath, $".{resourceName.ToLowerInvariant()}.reserve");
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        var reservation = new FileStream(reservationPath, fileOptions);
        try
        {
            if (Directory.EnumerateFileSystemEntries(runOutputPath)
                .Any(path => string.Equals(Path.GetFileName(path), resourceName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException($"The testing output path for resource '{resourceName}' already exists in '{runOutputPath}'.");
            }
            SocketPermissionHelper.CreateDirectory(resourceOutputPath, repairExisting: false);
        }
        finally
        {
            reservation.Dispose();
            File.Delete(reservationPath);
        }

        return resourceOutputPath;
    }
}
