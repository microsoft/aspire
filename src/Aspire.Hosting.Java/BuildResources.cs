// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Represents a Maven build resource that executes Maven goals before the main Java application starts.
/// </summary>
internal sealed class MavenBuildResource(string name, string workingDirectory, string[] args)
    : ExecutableResource(name, "mvnw", workingDirectory)
{
    /// <summary>
    /// Gets the arguments to pass to Maven.
    /// </summary>
    public string[] Args { get; } = args;
}

/// <summary>
/// Represents a Gradle build resource that executes Gradle tasks before the main Java application starts.
/// </summary>
internal sealed class GradleBuildResource(string name, string workingDirectory, string[] args)
    : ExecutableResource(name, "gradlew", workingDirectory)
{
    /// <summary>
    /// Gets the arguments to pass to Gradle.
    /// </summary>
    public string[] Args { get; } = args;
}
