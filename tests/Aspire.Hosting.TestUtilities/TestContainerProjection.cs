// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Testing;

/// <summary>
/// Provides a container projection that shares its owner's annotations for publisher tests.
/// </summary>
public sealed class TestContainerProjection(IResource owner) : ContainerResource(owner.Name)
{
    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => owner.Annotations;
}
