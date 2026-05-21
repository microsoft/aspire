// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Represents the annotation for the JavaScript package manager used in a resource.
/// </summary>
/// <param name="executableName">The name of the executable used to run the package manager.</param>
/// <param name="runScriptCommand">The command used to run a script with the JavaScript package manager.</param>
/// <param name="cacheMount">The BuildKit cache mount path for the package manager, or null if not supported.</param>
public sealed class JavaScriptPackageManagerAnnotation(string executableName, string? runScriptCommand, string? cacheMount = null) : IResourceAnnotation
{
    /// <summary>
    /// Gets the executable used to run the JavaScript package manager.
    /// </summary>
    public string ExecutableName { get; } = executableName;

    /// <summary>
    /// Gets the command used to run a script with the JavaScript package manager.
    /// </summary>
    public string? ScriptCommand { get; } = runScriptCommand;

    /// <summary>
    /// Gets the string used to separate individual commands in a command sequence, or <see langword="null"/> if one shouldn't be used.
    /// Defaults to "--".
    /// </summary>
    public string? CommandSeparator { get; init; } = "--";

    /// <summary>
    /// Gets the BuildKit cache mount path for the package manager, or null if not supported.
    /// </summary>
    public string? CacheMount { get; } = cacheMount;

    /// <summary>
    /// Gets the file patterns for package dependency files.
    /// </summary>
    public List<CopyFilePattern> PackageFilesPatterns { get; } = [];

    /// <summary>
    /// Gets or sets a callback to initialize the Docker build stage before installing packages.
    /// </summary>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public Action<DockerfileStage>? InitializeDockerBuildStage { get; init; }

    /// <summary>
    /// Gets or sets a callback to initialize the Docker runtime stage before configuring the entrypoint.
    /// </summary>
    [Experimental("ASPIREDOCKERFILEBUILDER001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal Action<DockerfileStage>? InitializeDockerRuntimeStage { get; init; }

    /// <summary>
    /// Gets or sets a callback to resolve the default <c>PublishAsNpmScript</c> runtime image from the build image.
    /// </summary>
    internal Func<string, string>? ResolveNpmScriptRuntimeImage { get; init; }
}
