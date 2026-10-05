// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// A resource that represents a build step that runs before its parent Java application starts.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="wrapperPath">The full path to the build tool's wrapper script.</param>
/// <param name="workingDirectory">The working directory to use for the command.</param>
/// <param name="tool">The build tool the wrapper invokes.</param>
/// <remarks>
/// The build tool is carried as data rather than expressed as a type per tool. Maven and Gradle build
/// steps differ only in the wrapper they exec and the arguments they are given, both of which are
/// already resolved from the parent resource's annotations, so a type apiece would describe nothing a
/// caller could not read from <paramref name="tool"/>.
/// </remarks>
internal sealed class JavaBuildResource(string name, string wrapperPath, string workingDirectory, JavaBuildTool tool)
    : ExecutableResource(name, wrapperPath, workingDirectory)
{
    /// <summary>
    /// Gets the build tool this step runs.
    /// </summary>
    public JavaBuildTool Tool { get; } = tool;
}
