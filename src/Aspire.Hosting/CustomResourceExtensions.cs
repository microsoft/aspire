// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for applying dashboard annotations to resources.
/// </summary>
public static class CustomResourceExtensions
{
    /// <summary>
    /// Initializes the resource with the initial snapshot.
    /// </summary>
    /// <typeparam name="TResource">The resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="initialSnapshot">The factory to create the initial <see cref="CustomResourceSnapshot"/> for this resource.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "CustomResourceSnapshot contains ResourcePropertySnapshot.Value (object?) and ResourceCommandSnapshot.Parameter (object?) — untyped properties not representable in statically-typed polyglot SDKs.")]
    public static IResourceBuilder<TResource> WithInitialState<TResource>(this IResourceBuilder<TResource> builder, CustomResourceSnapshot initialSnapshot)
        where TResource : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(initialSnapshot);

        return builder.WithAnnotation(new ResourceSnapshotAnnotation(initialSnapshot), ResourceAnnotationMutationBehavior.Replace);
    }
}
