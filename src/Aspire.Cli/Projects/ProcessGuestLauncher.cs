// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Launches a guest language process by starting a local OS process.
/// </summary>
internal sealed class ProcessGuestLauncher : IGuestProcessLauncher
{
    private readonly string _language;
    private readonly ILogger _logger;
    private readonly FileLoggerProvider? _fileLoggerProvider;
    private readonly Func<string, string?> _commandResolver;

    public ProcessGuestLauncher(string language, ILogger logger, FileLoggerProvider? fileLoggerProvider = null, Func<string, string?>? commandResolver = null)
    {
        _language = language;
        _logger = logger;
        _fileLoggerProvider = fileLoggerProvider;
        _commandResolver = commandResolver ?? PathLookupHelper.FindFullPathFromPath;
    }

    public async Task<(int ExitCode, OutputCollector? Output)> LaunchAsync(
        string command,
        string[] args,
        DirectoryInfo workingDirectory,
        IDictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        if (!CommandPathResolver.TryResolveCommand(command, _commandResolver, out var resolvedCommand, out var errorMessage))
        {
            _logger.LogError("Command '{Command}' not found in PATH", command);
            var errorOutput = new OutputCollector();
            errorOutput.AppendError(errorMessage!);
            return (-1, errorOutput);
        }

        _logger.LogDebug("Executing: {Command} {Args}", resolvedCommand, string.Join(" ", args));

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedCommand,
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in environmentVariables)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {resolvedCommand}");

        var outputCollector = new OutputCollector(_fileLoggerProvider, "AppHost");

        // Use ReadAllLinesAsync to multiplex stdout and stderr on a single loop,
        // replacing the previous OutputDataReceived/ErrorDataReceived event-based approach.
        await foreach (var line in process.ReadAllLinesAsync(cancellationToken))
        {
            if (line.StandardError)
            {
                _logger.LogTrace("{Language}({ProcessId}) stderr: {Line}", _language, process.Id, line.Content);
                outputCollector.AppendError(line.Content);
            }
            else
            {
                _logger.LogTrace("{Language}({ProcessId}) stdout: {Line}", _language, process.Id, line.Content);
                outputCollector.AppendOutput(line.Content);
            }
        }

        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, outputCollector);
    }
}
