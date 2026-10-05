// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Represents a Java application resource in the distributed application model.
/// </summary>
/// <param name="name">The name of the resource in the application model.</param>
/// <param name="workingDirectory">The working directory for the Java application.</param>
/// <remarks>
/// The command is always <c>java</c>. When the application is launched through a Maven goal or a Gradle
/// task, the wrapper script replaces that command; see <c>WithMavenGoal</c> and <c>WithGradleTask</c>.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public class JavaAppResource(string name, string workingDirectory)
    : ExecutableResource(name, "java", workingDirectory), IJavaAppResource, IResourceWithServiceDiscovery, IContainerFilesDestinationResource;
