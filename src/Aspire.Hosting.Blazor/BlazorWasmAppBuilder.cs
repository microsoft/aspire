// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
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
    public static async Task<bool> BuildAsync(string projectPath, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetDotNetCommandPath(),
            Arguments = $"build \"{projectPath}\"",
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        BlazorGatewayLog.BuildStarted(logger, projectPath);
        using var process = StartProcess(psi, logger, projectPath);
        if (process == null)
        {
            return false;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            BlazorGatewayLog.BuildFailed(logger, projectPath, stdout, stderr);
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
        string projectPath, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GetDotNetCommandPath(),
            Arguments = $"msbuild \"{projectPath}\" -t:ResolveStaticWebAssetsConfiguration -getProperty:StaticWebAssetEndpointsBuildManifestPath -getProperty:StaticWebAssetDevelopmentManifestPath",
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = StartProcess(psi, logger, projectPath);
        if (process == null)
        {
            return null;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            BlazorGatewayLog.MsBuildTargetFailed(logger, projectPath, stdout, stderr);
            return null;
        }

        var output = JsonSerializer.Deserialize(stdout.Trim(), ManifestJsonContext.Default.MSBuildPropertiesOutput);
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

    private static string GetDotNetCommandPath()
    {
        return Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnetHostPath
            ? dotnetHostPath
            : "dotnet";
    }

    private static Process? StartProcess(ProcessStartInfo startInfo, ILogger logger, string projectPath)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            BlazorGatewayLog.ProcessStartFailed(logger, startInfo.FileName, projectPath, ex.Message);
            return null;
        }
    }
}
