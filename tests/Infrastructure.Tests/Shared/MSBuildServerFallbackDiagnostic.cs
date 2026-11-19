// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Infrastructure.Tests;

/// <summary>
/// Simulates the dotnet CLI's MSBuild-server fallback diagnostic, which the CLI can print to standard output
/// under CI load when it fails to reach the MSBuild server. Tests that query MSBuild with
/// <c>-getItem</c>/<c>-getProperty</c> use this to verify they read results from <c>-getResultOutputFile</c>
/// instead of parsing standard output.
/// </summary>
internal static class MSBuildServerFallbackDiagnostic
{
    public const string Message =
        "MSBuild server unavailable: could not connect to the server within the timeout window; the server may have failed to start. Falling back to an in-process build.";

    /// <summary>
    /// Writes a wrapper (<c>.cmd</c> on Windows, <c>.sh</c> elsewhere) to <paramref name="directory"/> that runs
    /// <paramref name="dotnet"/> with its arguments, writes <see cref="Message"/> to standard output and exits
    /// with the real CLI's exit code.
    /// </summary>
    public static async Task<string> CreateDotNetWrapperAsync(string directory, string dotnet)
    {
        if (OperatingSystem.IsWindows())
        {
            var wrapper = Path.Combine(directory, "dotnet-with-diagnostic.cmd");
            await ExecutableFile.WriteAsync(wrapper, $"""
                @echo off
                "{dotnet}" %*
                set exitCode=%ERRORLEVEL%
                echo {Message}
                exit /b %exitCode%
                """);
            return wrapper;
        }

        var script = Path.Combine(directory, "dotnet-with-diagnostic.sh");
        await ExecutableFile.WriteAsync(script, $"""
            #!/bin/sh
            "{dotnet}" "$@"
            exitCode=$?
            echo '{Message}'
            exit $exitCode
            """);
        return script;
    }
}
