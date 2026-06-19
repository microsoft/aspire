// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Represents a metadata annotation that specifies the build tool used to run the Java application.
/// </summary>
/// <param name="wrapperPath">The full path to the build tool wrapper script.</param>
/// <param name="args">The arguments to pass to the build tool (e.g., the goal or task name).</param>
internal sealed class JavaBuildToolAnnotation(string wrapperPath, string[] args) : IResourceAnnotation
{
    /// <summary>
    /// The full path to the build tool wrapper script (e.g., mvnw or gradlew).
    /// </summary>
    public string WrapperPath { get; } = wrapperPath;

    /// <summary>
    /// The arguments to pass to the build tool.
    /// </summary>
    public string[] Args { get; } = args;
}
