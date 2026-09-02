// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

internal sealed class DistributedApplicationResourceBuilder<T>(IDistributedApplicationBuilder applicationBuilder, T resource) : IResourceBuilder<T> where T : IResource
{
    public T Resource { get; } = resource;
    public IDistributedApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

    /// <inheritdoc />
    public IResourceBuilder<T> WithAnnotation<TAnnotation>(TAnnotation annotation, ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append) where TAnnotation : IResourceAnnotation
    {
        ArgumentNullException.ThrowIfNull(annotation);

        // Some defensive code to protect against introducing a new enumeration value without first updating
        // this code to accommodate it.
        if (behavior != ResourceAnnotationMutationBehavior.Append && behavior != ResourceAnnotationMutationBehavior.Replace)
        {
            throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "ResourceAnnotationMutationBehavior must be either Append or Replace.");
        }

        // If the behavior is AddReplace then there should never be more than one annotation present. The following call will result in an exception which
        // allows us to easily spot these bugs.
        if (behavior == ResourceAnnotationMutationBehavior.Replace && Resource.Annotations.OfType<TAnnotation>().SingleOrDefault() is { } existingAnnotation)
        {
            Resource.Annotations.Remove(existingAnnotation);
        }

        Resource.Annotations.Add(annotation);
        return this;
    }
}
