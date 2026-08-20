// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Tracks locations declared by the application model separately from mutable provisioning overrides.
/// </summary>
internal static class AzureDeclaredLocation
{
    public static bool IsSet(AzureBicepResource resource) =>
        resource.HasAnnotationOfType<AzureDeclaredLocationAnnotation>();

    public static void Set(AzureBicepResource resource, object location)
    {
        resource.Parameters[AzureBicepResource.KnownParameters.Location] = location;
        resource.Annotations.Add(new AzureDeclaredLocationAnnotation());
    }

    private sealed class AzureDeclaredLocationAnnotation : IResourceAnnotation;
}
