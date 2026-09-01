// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.BinaryCompatibility;

public sealed class LegacyResource : IResource
{
    public string Name => "legacy";

    public ResourceAnnotationCollection Annotations { get; } = new();

    // This method is compiled against Aspire.Hosting 13.5 so the test exercises the
    // Collection<IResourceAnnotation>.Add method token emitted for existing integrations.
    public int MutateAnnotations()
    {
        Annotations.Add(new LegacyAnnotation());

        return Annotations.Count;
    }

    private sealed class LegacyAnnotation : IResourceAnnotation;
}
