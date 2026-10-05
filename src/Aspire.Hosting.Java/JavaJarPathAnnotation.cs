// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Records that a Java application runs a prebuilt JAR through <c>java -jar</c>.
/// </summary>
/// <remarks>
/// Held as an annotation rather than a property on <see cref="JavaAppResource"/> so run, publish, and
/// debug all read one source of truth, and so the launch mode cannot be changed after the resource has
/// been configured.
/// </remarks>
/// <param name="JarPath">The authored path to the JAR file to execute, either absolute or relative to the application's working directory.</param>
internal sealed record JavaJarPathAnnotation(string JarPath) : IResourceAnnotation;

/// <summary>
/// Records an explicitly configured main class, used when an IDE launches the application itself.
/// </summary>
/// <param name="MainClass">The fully qualified name of the class declaring <c>main</c>.</param>
internal sealed record JavaMainClassAnnotation(string MainClass) : IResourceAnnotation;

/// <summary>
/// Records the JAR produced by a container build, relative to the build stage's working directory.
/// </summary>
/// <param name="RelativePath">The path to the JAR, relative to the application directory.</param>
internal sealed record JavaJarArtifactAnnotation(string RelativePath) : IResourceAnnotation;
