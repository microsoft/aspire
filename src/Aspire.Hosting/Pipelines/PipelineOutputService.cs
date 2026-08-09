// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Default implementation of <see cref="IPipelineOutputService"/>.
/// </summary>
[Experimental("ASPIREPIPELINES004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
internal sealed class PipelineOutputService : IPipelineOutputService
{
    /// <summary>
    /// Stores the resolved output directory path, or <c>null</c> if not specified.
    /// </summary>
    private readonly string? _outputPath;

    /// <summary>
    /// Stores the default output directory path.
    /// </summary>
    private readonly string _defaultOutputPath;

    /// <summary>
    /// Stores the path to the temporary directory for pipeline output.
    /// </summary>
    private readonly string _tempDirectory;

    public PipelineOutputService(IOptions<PipelineOptions> options, IConfiguration configuration, IFileSystemService directoryService)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(directoryService);

        _outputPath = options.Value.OutputPath is not null ? Path.GetFullPath(options.Value.OutputPath) : null;
        _defaultOutputPath = _outputPath is null
            ? ResolveDefaultOutputPath(configuration)
            : Path.Combine(Environment.CurrentDirectory, "aspire-output");
        _tempDirectory = directoryService.TempDirectory.CreateTempSubdirectory("aspire-pipelines").Path;
    }

    private static string ResolveDefaultOutputPath(IConfiguration configuration)
    {
        // DistributedApplicationBuilder publishes the resolved AppHost directory here.
        var appHostDirectory = configuration["AppHost:Directory"];
        var baseDirectory = Environment.CurrentDirectory;

        if (!string.IsNullOrWhiteSpace(appHostDirectory))
        {
            try
            {
                baseDirectory = Path.GetFullPath(appHostDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
            {
                baseDirectory = Environment.CurrentDirectory;
            }
        }

        return Path.Combine(baseDirectory, "aspire-output");
    }

    /// <inheritdoc/>
    public string GetOutputDirectory()
    {
        return _outputPath ?? _defaultOutputPath;
    }

    /// <inheritdoc/>
    public string GetOutputDirectory(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var baseOutputDir = GetOutputDirectory();
        return Path.Combine(baseOutputDir, resource.Name);
    }

    /// <inheritdoc/>
    public string GetTempDirectory()
    {
        return _tempDirectory;
    }

    /// <inheritdoc/>
    public string GetTempDirectory(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var baseTempDir = GetTempDirectory();
        return Path.Combine(baseTempDir, resource.Name);
    }
}
