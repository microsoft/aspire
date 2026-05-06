// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal static partial class BlazorGatewayLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to build {ResourceName}")]
    public static partial void FailedToBuild(ILogger logger, string resourceName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve manifest paths for {ResourceName}")]
    public static partial void FailedToResolveManifests(ILogger logger, string resourceName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Discovered manifests for '{ResourceName}': Endpoints={EndpointsPath}, Runtime={RuntimePath}")]
    public static partial void DiscoveredManifests(ILogger logger, string resourceName, string endpointsPath, string runtimePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Endpoints manifest not found: {ManifestPath}")]
    public static partial void EndpointsManifestNotFound(ILogger logger, string manifestPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote prefixed endpoints for '{ResourceName}' to {DestPath}")]
    public static partial void WrotePrefixedEndpoints(ILogger logger, string resourceName, string destPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Building: dotnet build \"{ProjectPath}\"")]
    public static partial void BuildStarted(ILogger logger, string projectPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Build failed for {ProjectPath}:\n{Stdout}\n{Stderr}")]
    public static partial void BuildFailed(ILogger logger, string projectPath, string stdout, string stderr);

    [LoggerMessage(Level = LogLevel.Information, Message = "Build succeeded: {ProjectName}")]
    public static partial void BuildSucceeded(ILogger logger, string projectName);

    [LoggerMessage(Level = LogLevel.Error, Message = "ResolveStaticWebAssetsConfiguration failed for {ProjectPath}:\n{Stdout}\n{Stderr}")]
    public static partial void MsBuildTargetFailed(ILogger logger, string projectPath, string stdout, string stderr);

    [LoggerMessage(Level = LogLevel.Error, Message = "ResolveStaticWebAssetsConfiguration returned incomplete paths: Endpoints='{EndpointsPath}', Runtime='{RuntimePath}'")]
    public static partial void IncompleteManifestPaths(ILogger logger, string? endpointsPath, string? runtimePath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to start '{Command}' while processing {ProjectPath}: {ErrorMessage}")]
    public static partial void ProcessStartFailed(ILogger logger, string command, string projectPath, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime manifest not found: {ManifestPath}")]
    public static partial void RuntimeManifestNotFound(ILogger logger, string manifestPath);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Merged runtime manifest for '{ResourceName}' under '{Prefix}/' (offset={Offset}, roots={RootCount})")]
    public static partial void MergedRuntimeManifest(ILogger logger, string resourceName, string prefix, int offset, int rootCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote merged runtime manifest to {OutputPath}")]
    public static partial void WroteMergedManifest(ILogger logger, string outputPath);
}
