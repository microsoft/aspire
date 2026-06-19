// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a Maven build step.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="wrapperScript">The full path to the Maven wrapper script.</param>
/// <param name="workingDirectory">The working directory to use for the command.</param>
public class MavenBuildResource(string name, string wrapperScript, string workingDirectory)
    : ExecutableResource(name, wrapperScript, workingDirectory);

/// <summary>
/// A resource that represents a Gradle build step.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="wrapperScript">The full path to the Gradle wrapper script.</param>
/// <param name="workingDirectory">The working directory to use for the command.</param>
public class GradleBuildResource(string name, string wrapperScript, string workingDirectory)
    : ExecutableResource(name, wrapperScript, workingDirectory);
