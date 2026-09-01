// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet;

internal sealed record DotnetProjectRunProperties(
    string Command,
    string Arguments,
    string? WorkingDirectory);

internal static class DotnetProjectRunPropertiesResolver
{
    public static async Task<DotnetProjectRunProperties> ResolveAsync(
        string projectPath,
        string? buildConfiguration,
        IReadOnlyDictionary<string, string> buildEnvironment,
        string workingDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var responseFile = await DotnetProjectBuildEnvironment.CreateResponseFileAsync(
            buildEnvironment,
            logger,
            cancellationToken).ConfigureAwait(false);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-target:ComputeRunArguments");
        startInfo.ArgumentList.Add("-getProperty:RunCommand,RunArguments,RunWorkingDirectory");
        startInfo.ArgumentList.Add("-v:q");
        if (!string.IsNullOrEmpty(buildConfiguration))
        {
            startInfo.ArgumentList.Add($"-property:Configuration={buildConfiguration}");
        }

        foreach (var (name, value) in buildEnvironment)
        {
            startInfo.Environment[name] = value;
        }
        if (responseFile is not null)
        {
            startInfo.ArgumentList.Add(responseFile.Argument);
        }
        startInfo.ArgumentList.Add("-property:GenerateFullPaths=true");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new DistributedApplicationException(
                $"Failed to start dotnet to resolve the run command for project '{projectPath}'.",
                ex);
        }

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            logger.LogDebug(
                "dotnet msbuild failed while resolving the run command for project {ProjectPath}. Standard output: {StandardOutput} Standard error: {StandardError}",
                projectPath,
                DotnetProjectBuildEnvironment.RedactEnvironmentValues(standardOutput, buildEnvironment),
                DotnetProjectBuildEnvironment.RedactEnvironmentValues(standardError, buildEnvironment));
            throw new DistributedApplicationException(
                $"dotnet msbuild failed with exit code {process.ExitCode} while resolving the run command for project '{projectPath}'.");
        }

        try
        {
            // Multiple -getProperty values produce:
            //   { "Properties": { "RunCommand": "...", "RunArguments": "...", "RunWorkingDirectory": "..." } }
            using var document = JsonDocument.Parse(standardOutput);
            var properties = document.RootElement.GetProperty("Properties");
            var command = properties.GetProperty("RunCommand").GetString();
            var arguments = properties.GetProperty("RunArguments").GetString() ?? string.Empty;
            var runWorkingDirectory = properties.GetProperty("RunWorkingDirectory").GetString();
            var normalizedCommand = command?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalizedCommand))
            {
                throw new DistributedApplicationException(
                    $"dotnet msbuild returned an empty run command for project '{projectPath}'.");
            }

            return new(normalizedCommand, arguments, runWorkingDirectory);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DistributedApplicationException(
                $"dotnet msbuild returned an invalid run-command response for project '{projectPath}'.",
                ex);
        }
    }
}
