// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests.TestServices;

internal sealed class TestContainerImageRegistryAssociationAnnotation(IResource registry)
    : ContainerImageRegistryAssociationAnnotation(registry, defaultEnvironment: null);
