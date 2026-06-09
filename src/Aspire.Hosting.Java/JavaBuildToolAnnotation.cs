// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Stores the Maven or Gradle build tool configuration for a Java application.
/// </summary>
internal sealed class JavaBuildToolAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets the type of build tool (Maven or Gradle).
    /// </summary>
    public required JavaBuildTool BuildTool { get; init; }

    /// <summary>
    /// Gets or sets the path to the wrapper executable (mvnw, mvnw.cmd, gradlew, gradlew.bat).
    /// </summary>
    public required string WrapperPath { get; init; }

    /// <summary>
    /// Gets or sets the arguments to pass to the build tool.
    /// </summary>
    public required string[] Args { get; init; }
}

/// <summary>
/// Enumerates the supported Java build tools.
/// </summary>
internal enum JavaBuildTool
{
    /// <summary>
    /// Apache Maven build tool.
    /// </summary>
    Maven,

    /// <summary>
    /// Gradle build tool.
    /// </summary>
    Gradle
}
