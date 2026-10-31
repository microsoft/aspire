// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for marking resources as participants in an <c>aspire test</c> run.
/// </summary>
public static class TestResourceBuilderExtensions
{
    /// <summary>
    /// Marks a resource whose completion bounds an <c>aspire test</c> run.
    /// </summary>
    /// <ats-summary>Marks a resource as a test resource.</ats-summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The command waits for all marked resources, including their replicas, then shuts down the application.
    /// A terminal state with exit code zero passes; startup failure, a nonzero exit code, or an unknown
    /// terminal exit code fails. Resources must publish completion through <see cref="ResourceNotificationService"/>.
    /// The annotation does not change the lifetime of <c>aspire run</c>.
    /// </para>
    /// <example>
    /// <code lang="C#">
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api");
    /// builder.AddExecutable("tests", "test-runner", ".")
    ///     .WithReference(api)
    ///     .WaitFor(api)
    ///     .WithTestRun();
    /// </code>
    /// </example>
    /// </remarks>
    /// <ats-remarks>
    /// Waits for every marked resource, including replicas, then shuts down the application.
    /// Exit code zero passes. Startup failure, a nonzero exit code, or an unknown terminal exit code fails.
    /// This does not change the lifetime of aspire run.
    /// </ats-remarks>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    [AspireExport]
    public static IResourceBuilder<T> WithTestRun<T>(this IResourceBuilder<T> builder) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new TestAnnotation());
    }
}
