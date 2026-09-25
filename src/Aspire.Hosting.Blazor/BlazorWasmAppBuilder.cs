// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Builds Blazor WASM app projects and discovers their static web asset manifest paths
/// by shelling out to the dotnet CLI.
/// </summary>
internal static class BlazorWasmAppBuilder
{
    /// <summary>
    /// Builds a single WASM app via <c>dotnet build</c>.
    /// </summary>
    public static async Task<bool> BuildAsync(string projectPath, ILogger logger, CancellationToken cancellationToken)
    {
        BlazorGatewayLog.BuildStarted(logger, projectPath);
        var result = await BlazorDotNetCliRunner.RunAsync(
            projectPath,
            "build",
            [],
            machineReadableOutput: false,
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            BlazorGatewayLog.ProcessStartFailed(
                logger,
                result.Command,
                projectPath,
                result.StartException?.Message ?? "Process.Start returned null.");
            return false;
        }

        if (result.ExitCode != 0)
        {
            BlazorGatewayLog.BuildFailed(logger, projectPath, result.StandardOutput, result.StandardError);
            return false;
        }

        BlazorGatewayLog.BuildSucceeded(logger, Path.GetFileNameWithoutExtension(projectPath));
        return true;
    }

    /// <summary>
    /// Invokes the built-in <c>ResolveStaticWebAssetsConfiguration</c> MSBuild target to
    /// discover the endpoints and development manifest file paths via <c>-getProperty</c>.
    /// </summary>
    public static async Task<(string endpointsManifest, string runtimeManifest)?> GetManifestPathsAsync(
        string projectPath, ILogger logger, CancellationToken cancellationToken)
    {
        var result = await BlazorDotNetCliRunner.RunAsync(
            projectPath,
            "msbuild",
            [
                "-t:ResolveStaticWebAssetsConfiguration",
                "-getProperty:StaticWebAssetEndpointsBuildManifestPath",
                "-getProperty:StaticWebAssetDevelopmentManifestPath",
                "-nologo"
            ],
            machineReadableOutput: true,
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            BlazorGatewayLog.ProcessStartFailed(
                logger,
                result.Command,
                projectPath,
                result.StartException?.Message ?? "Process.Start returned null.");
            return null;
        }

        if (result.ExitCode != 0)
        {
            BlazorGatewayLog.MsBuildTargetFailed(logger, projectPath, result.StandardOutput, result.StandardError);
            return null;
        }

        MSBuildPropertiesOutput? output;
        try
        {
            output = JsonSerializer.Deserialize(result.StandardOutput.Trim(), ManifestJsonContext.Default.MSBuildPropertiesOutput);
        }
        catch (JsonException ex)
        {
            BlazorGatewayLog.ManifestJsonParseFailed(logger, projectPath, ex);
            return null;
        }

        var props = output?.Properties;

        if (props == null
            || string.IsNullOrEmpty(props.StaticWebAssetEndpointsBuildManifestPath)
            || string.IsNullOrEmpty(props.StaticWebAssetDevelopmentManifestPath))
        {
            BlazorGatewayLog.IncompleteManifestPaths(logger,
                props?.StaticWebAssetEndpointsBuildManifestPath, props?.StaticWebAssetDevelopmentManifestPath);
            return null;
        }

        var projectDir = Path.GetDirectoryName(projectPath)!;
        var endpoints = Path.GetFullPath(Path.Combine(projectDir, props.StaticWebAssetEndpointsBuildManifestPath));
        var runtime = Path.GetFullPath(Path.Combine(projectDir, props.StaticWebAssetDevelopmentManifestPath));

        return (endpoints, runtime);
    }
}
