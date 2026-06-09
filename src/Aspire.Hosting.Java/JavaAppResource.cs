// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Represents a Java application resource in the distributed application model.
/// </summary>
/// <param name="name">The name of the resource in the application model.</param>
/// <param name="workingDirectory">The working directory for the Java application.</param>
[AspireExport(ExposeProperties = true)]
public class JavaAppResource(string name, string workingDirectory)
    : ExecutableResource(name, "java", workingDirectory), IResourceWithServiceDiscovery, IContainerFilesDestinationResource
{
    /// <summary>
    /// Gets or sets the path to the JAR file to execute.
    /// </summary>
    /// <remarks>
    /// When set, the resource will execute the JAR file using <c>java -jar &lt;jarPath&gt;</c>.
    /// </remarks>
    public string? JarPath { get; set; }
}
