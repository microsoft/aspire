// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Represents a Java application that runs from a prebuilt container image.
/// </summary>
/// <param name="name">The name of the resource in the application model.</param>
/// <param name="entrypoint">An optional entrypoint that replaces the image's own.</param>
[AspireExport(ExposeProperties = true)]
public class JavaContainerResource(string name, string? entrypoint = null)
    : ContainerResource(name, entrypoint), IJavaAppResource, IResourceWithServiceDiscovery;
