// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Marks a resource that runs a Java application, whether as a local process or as a container.
/// </summary>
/// <remarks>
/// Implemented by <see cref="JavaAppResource"/> and <see cref="JavaContainerResource"/> so that
/// configuration which is meaningful for any JVM, such as
/// <see cref="JavaHostingExtensions.WithJvmArgs{T}(IResourceBuilder{T}, string[])"/>, applies to both
/// without also appearing on unrelated executables and containers.
/// </remarks>
public interface IJavaAppResource : IResourceWithEnvironment;
