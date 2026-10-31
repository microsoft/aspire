// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared;

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
    /// </ats-remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    [Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
    public static IResourceBuilder<T> WithTesting<T>(this IResourceBuilder<T> builder, Action<TestingOptions>? callback = null) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        TestRunContext? runContext = null;
        string? resourceOutputPath = null;
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode &&
            builder.ApplicationBuilder.Configuration[KnownConfigNames.TestOutputPath] is { } outputPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            runContext = new TestRunContext(Path.GetFullPath(outputPath));
            // The CLI creates the run directory before model construction. Allocate only within
            // that private boundary so immediate configuration can safely use a real destination.
            SocketPermissionHelper.CreateDirectory(runContext.OutputPath, repairExisting: false);
            var resourcesPath = Path.Combine(runContext.OutputPath, "resources");
            SocketPermissionHelper.CreateDirectory(resourcesPath, repairExisting: false);
            resourceOutputPath = Path.Combine(resourcesPath, Guid.NewGuid().ToString("N"));
            SocketPermissionHelper.CreateDirectory(resourceOutputPath, repairExisting: false);
        }
        var options = new TestingOptions(runContext, builder.Resource.Name, resourceOutputPath);
        callback?.Invoke(options);
        options.CompleteConfiguration();

        return builder.WithAnnotation(new TestingAnnotation(options), ResourceAnnotationMutationBehavior.Replace);
    }
}
