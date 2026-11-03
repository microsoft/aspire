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
    /// Marks a resource for an <c>aspire test</c> run, with an optional asynchronous runtime result collector.
    /// </summary>
    /// <ats-summary>Marks a resource for testing with an optional runtime result collector.</ats-summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">An optional asynchronous result collector that runs alongside resource execution during testing.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The callback does not run during model definition. It starts alongside resource execution and may
    /// use <see cref="ResourceTestingContext.WaitForCompletionAsync"/> before harvesting final artifacts.
    /// Remote collectors use test-run cancellation rather than the ordinary remote callback timeout.
    /// The command waits for all marked resources, including their replicas, and all collectors.
    /// It then shuts down the application unless <c>--keep-alive</c> was requested.
    /// A terminal state with exit code zero passes; startup failure, a nonzero exit code, or an unknown
    /// terminal exit code fails. Resources must publish completion through <see cref="ResourceNotificationService"/>.
    /// The annotation does not change the lifetime of <c>aspire run</c>.
    /// Published failures or collector exceptions fail testing without discarding other results.
    /// Outside testing, the callback does not run and no testing output paths are allocated.
    /// Framework helpers must configure their runner's report destination independently, then stage
    /// collected artifacts into <see cref="ResourceTestingContext.OutputPath"/> at runtime.
    /// </para>
    /// <para>
    /// Calling this method again replaces the collector, including removing it for a marker-only call,
    /// while preserving the resource's existing run context and output directory.
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
    /// The callback runs at runtime alongside the resource, not during model definition or ordinary runs.
    /// The context supplies a private resource output destination. Configure runner report paths independently
    /// and stage collected artifacts into this destination at runtime.
    /// Repeated calls replace the collector while preserving the existing run context and output directory.
    /// </ats-remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    /// <exception cref="ArgumentException">The resource name cannot be used as a portable testing output directory.</exception>
    /// <exception cref="IOException">A new resource's testing output path already exists or cannot be created securely.</exception>
    [AspireExport]
    [Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
    public static IResourceBuilder<T> WithTesting<T>(this IResourceBuilder<T> builder,
        [AspireCallbackTimeout(Timeout.Infinite)] Func<ResourceTestingContext, Task>? callback = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.TryGetLastAnnotation<TestingAnnotation>(out var annotation);
        var testingContext = annotation?.TestingContext;
        var resourceOutputPath = annotation?.OutputPath;
        if (testingContext is null &&
            builder.ApplicationBuilder.ExecutionContext.IsRunMode &&
            builder.ApplicationBuilder.Configuration[KnownConfigNames.TestOutputPath] is { } outputPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            testingContext = new TestingContext(Path.GetFullPath(outputPath));
            // The CLI creates the run directory before model construction. Allocate only within
            // that private boundary before execution, without invoking the runtime collector.
            SocketPermissionHelper.CreateDirectory(testingContext.OutputPath, repairExisting: false);
            resourceOutputPath = CreateResourceOutputDirectory(testingContext.OutputPath, builder.Resource.Name);
        }
        return builder.WithAnnotation(new TestingAnnotation
        {
            Collector = callback,
            TestingContext = testingContext,
            OutputPath = resourceOutputPath
        }, ResourceAnnotationMutationBehavior.Replace);
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
            // The reservation and existence check establish a new, owned directory.
            // Reuse the shared directory helper to restore owner access removed by umask.
            SocketPermissionHelper.CreateDirectory(resourceOutputPath, repairExisting: true);
        }
        finally
        {
            reservation.Dispose();
            File.Delete(reservationPath);
        }

        return resourceOutputPath;
    }
}
