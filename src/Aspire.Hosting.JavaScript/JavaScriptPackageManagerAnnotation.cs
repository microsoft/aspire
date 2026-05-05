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
    /// When set, builds the argv for invoking a script on a single workspace member using this package
    /// manager's native workspace filter syntax. Inputs: workspace name, script name, script args.
    /// Returns the full argv including the executable name.
    /// </summary>
    /// <remarks>
    /// Examples:
    /// <list type="bullet">
    ///   <item><description>pnpm: <c>pnpm --filter &lt;name&gt; run &lt;script&gt; [args]</c></description></item>
    ///   <item><description>yarn: <c>yarn workspace &lt;name&gt; run &lt;script&gt; [args]</c></description></item>
    ///   <item><description>npm: <c>npm run &lt;script&gt; --workspace=&lt;name&gt; [-- args]</c></description></item>
    ///   <item><description>bun: <c>bun --filter &lt;name&gt; run &lt;script&gt; [args]</c></description></item>
    /// </list>
    /// </remarks>
    internal Func<string, string, IReadOnlyList<string>, IReadOnlyList<string>>? WorkspaceCommandFactory { get; init; }
}
