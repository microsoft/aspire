// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for marking resources as test resources in an <c>aspire test</c> run.
/// </summary>
public static class TestResourceBuilderExtensions
{
    /// <summary>
    /// Adds a <see cref="TestAnnotation"/> to the resource so that it participates in an
    /// <c>aspire test</c> run and bounds the run's lifetime.
    /// </summary>
    /// <ats-summary>Marks a resource as a test resource</ats-summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// When the app host is launched with <c>aspire test</c>, the run stays alive until every resource
    /// marked with <see cref="WithTestRun{T}(IResourceBuilder{T})"/> reaches a terminal state, then the
    /// app host shuts down automatically. Outside of <c>aspire test</c> (for example under
    /// <c>aspire run</c>) the annotation has no effect on the lifetime.
    /// </para>
    /// <example>
    /// A test project runs after the API it exercises is available, and bounds the test run.
    /// <code lang="C#">
    /// var builder = DistributedApplication.CreateBuilder(args);
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api");
    /// builder.AddProject&lt;Projects.IntegrationTests&gt;("integration-tests")
    ///        .WithReference(api)
    ///        .WaitFor(api)
    ///        .WithTestRun();
    /// </code>
    /// </example>
    /// </remarks>
    /// <ats-remarks />
    [AspireExport]
    public static IResourceBuilder<T> WithTestRun<T>(this IResourceBuilder<T> builder) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new TestAnnotation());
    }
}
