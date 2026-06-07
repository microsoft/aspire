// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Cpp;

/// <summary>
/// Represents a C++ application built by CMake in the distributed application model.
/// </summary>
/// <remarks>
/// <para>
/// This resource allows C++ applications that use CMake to run as part of a distributed application.
/// The resource configures the CMake build directory, executes the selected CMake target, and can
/// expose endpoints for service discovery like other Aspire resources.
/// </para>
/// <para>
/// Aspire injects OpenTelemetry environment variables for this resource when configured by
/// <c>AddCMakeApp</c>, but the C++ application must use an OpenTelemetry-capable library or SDK
/// to emit telemetry.
/// </para>
/// </remarks>
/// <example>
/// Add a C++ HTTP API built with CMake:
/// <code lang="csharp">
/// var builder = DistributedApplication.CreateBuilder(args);
///
/// builder.AddCMakeApp("api", "../cpp-api", targetName: "api")
///        .WithHttpEndpoint(env: "PORT");
///
/// builder.Build().Run();
/// </code>
/// </example>
public class CMakeAppResource : ExecutableResource, IResourceWithServiceDiscovery, IContainerFilesDestinationResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CMakeAppResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource in the distributed application model.</param>
    /// <param name="sourceDirectory">The directory containing the CMake project.</param>
    /// <param name="buildDirectory">The directory used for CMake configure and build output.</param>
    /// <param name="targetName">The CMake target to build before running the application.</param>
    /// <param name="executablePath">The path to the executable produced by the CMake target.</param>
    /// <param name="runtimeOutputDirectory">The directory passed to CMake for executable target output.</param>
    public CMakeAppResource(
        string name,
        string sourceDirectory,
        string buildDirectory,
        string targetName,
        string executablePath,
        string runtimeOutputDirectory)
        : base(name, executablePath, sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceDirectory);
        ArgumentException.ThrowIfNullOrEmpty(buildDirectory);
        ArgumentException.ThrowIfNullOrEmpty(targetName);
        ArgumentException.ThrowIfNullOrEmpty(runtimeOutputDirectory);

        SourceDirectory = sourceDirectory;
        BuildDirectory = buildDirectory;
        TargetName = targetName;
        RuntimeOutputDirectory = runtimeOutputDirectory;
    }

    /// <summary>
    /// Gets the directory containing the CMake project.
    /// </summary>
    public string SourceDirectory { get; }

    /// <summary>
    /// Gets the directory used for CMake configure and build output.
    /// </summary>
    public string BuildDirectory { get; }

    /// <summary>
    /// Gets the CMake target to build before running the application.
    /// </summary>
    public string TargetName { get; }

    /// <summary>
    /// Gets the directory passed to CMake for executable target output.
    /// </summary>
    public string RuntimeOutputDirectory { get; }
}

