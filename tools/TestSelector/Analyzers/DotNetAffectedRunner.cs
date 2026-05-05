// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;

namespace TestSelector.Analyzers;

/// <summary>
/// Runs the dotnet-affected CLI tool and parses its output.
/// </summary>
public sealed class DotNetAffectedRunner
{
    private readonly string _solutionPath;
    private readonly string _workingDirectory;
    private readonly bool _verbose;

    public DotNetAffectedRunner(string solutionPath, string workingDirectory, bool verbose = false)
    {
        _solutionPath = solutionPath;
        _workingDirectory = workingDirectory;
        _verbose = verbose;
    }

    /// <summary>
    /// Runs dotnet-affected with the specified options.
    /// </summary>
    /// <param name="fromRef">The git ref to compare from (e.g., "origin/main").</param>
    /// <param name="toRef">The git ref to compare to (default: HEAD).</param>
    /// <returns>The result containing affected projects.</returns>
    public async Task<DotNetAffectedResult> RunAsync(string fromRef, string? toRef = null)
    {
        // dotnet-affected --format json writes to affected.json in the working directory.
        // Delete any stale file before running to avoid reading outdated results.
        var affectedJsonPath = Path.Combine(_workingDirectory, "affected.json");
        if (File.Exists(affectedJsonPath))
        {
            File.Delete(affectedJsonPath);
        }

        var args = new List<string>
        {
            "affected",
            "--format", "json",
            "--solution-path", _solutionPath,
            "--from", fromRef
        };

        if (!string.IsNullOrEmpty(toRef))
        {
            args.Add("--to");
            args.Add(toRef);
        }

        if (_verbose)
        {
            args.Add("--verbose");
        }

        var result = await RunProcessAsync("dotnet", args, _workingDirectory).ConfigureAwait(false);

        if (!result.Success)
        {
            // Build a comprehensive error message
            var errorParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                errorParts.Add($"Exception: {result.Error}");
            }
            if (!string.IsNullOrWhiteSpace(result.StdErr))
            {
                errorParts.Add($"stderr: {result.StdErr.Trim()}");
            }
            if (!string.IsNullOrWhiteSpace(result.StdOut))
            {
                errorParts.Add($"stdout: {result.StdOut.Trim()}");
            }

            var errorMessage = errorParts.Count > 0
                ? string.Join("; ", errorParts)
                : $"Process exited with code {result.ExitCode} (no output)";

            return new DotNetAffectedResult
            {
                Success = false,
                Error = errorMessage,
                StdOut = result.StdOut,
                StdErr = result.StdErr,
                ExitCode = result.ExitCode
            };
        }

        try
        {
            // dotnet-affected --format json writes results to affected.json on disk,
            // not to stdout (stdout contains a human-readable table).
            List<string> projects;
            if (File.Exists(affectedJsonPath))
            {
                var jsonContent = await File.ReadAllTextAsync(affectedJsonPath).ConfigureAwait(false);
                projects = ParseOutput(jsonContent);
            }
            else
            {
                // Fallback: try parsing stdout in case behavior changes
                projects = ParseOutput(result.StdOut);
            }

            // Normalize absolute paths to relative paths from the working directory.
            // dotnet-affected returns absolute FilePath values, but downstream consumers
            // (glob matchers, enumerate-tests) expect relative paths like "tests/Foo.Tests/Foo.Tests.csproj".
            var workDir = _workingDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            projects = projects.Select(p =>
            {
                if (Path.IsPathRooted(p) && p.StartsWith(workDir, StringComparison.OrdinalIgnoreCase))
                {
                    return p[workDir.Length..].Replace('\\', '/');
                }
                return p.Replace('\\', '/');
            }).ToList();

            return new DotNetAffectedResult
            {
                Success = true,
                AffectedProjects = projects,
                StdOut = result.StdOut,
                StdErr = result.StdErr,
                ExitCode = result.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new DotNetAffectedResult
            {
                Success = false,
                Error = $"Failed to parse dotnet-affected output: {ex.Message}",
                RawOutput = result.StdOut,
                StdOut = result.StdOut,
                StdErr = result.StdErr,
                ExitCode = result.ExitCode
            };
        }
    }

    internal static List<string> ParseOutput(string output)
    {
        var projects = new List<string>();

        if (string.IsNullOrWhiteSpace(output))
        {
            return projects;
        }

        // dotnet-affected outputs JSON array of project paths
        // Try to parse as JSON first
        try
        {
            using var doc = JsonDocument.Parse(output);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    // Handle direct string or object with path property.
                    // dotnet-affected uses "FilePath" as the key.
                    var path = element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : element.TryGetProperty("FilePath", out var filePathProp)
                            ? filePathProp.GetString()
                            : element.TryGetProperty("path", out var pathProp)
                                ? pathProp.GetString()
                                : element.TryGetProperty("ProjectPath", out var projPathProp)
                                    ? projPathProp.GetString()
                                    : null;

                    if (!string.IsNullOrEmpty(path))
                    {
                        projects.Add(path);
                    }
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Handle object with "projects" or "affectedProjects" array
                if (doc.RootElement.TryGetProperty("projects", out var projectsArray) ||
                    doc.RootElement.TryGetProperty("affectedProjects", out projectsArray))
                {
                    foreach (var element in projectsArray.EnumerateArray())
                    {
                        var path = element.ValueKind == JsonValueKind.String
                            ? element.GetString()
                            : element.TryGetProperty("path", out var pathProp)
                                ? pathProp.GetString()
                                : null;

                        if (!string.IsNullOrEmpty(path))
                        {
                            projects.Add(path);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // If JSON parsing fails, try line-by-line parsing
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(trimmed);
                }
            }
        }

        return projects;
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            var stdOut = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stdErr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

            await process.WaitForExitAsync().ConfigureAwait(false);

            return new ProcessResult
            {
                // Exit code 166 means no affected projects were found, which is not an error
                Success = process.ExitCode == 0 || process.ExitCode == 166,
                ExitCode = process.ExitCode,
                StdOut = stdOut,
                StdErr = stdErr
            };
        }
        catch (Exception ex)
        {
            return new ProcessResult
            {
                Success = false,
                ExitCode = -1,
                Error = ex.Message
            };
        }
    }

    private sealed class ProcessResult
    {
        public bool Success { get; init; }
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
        public string? Error { get; init; }
    }
}
