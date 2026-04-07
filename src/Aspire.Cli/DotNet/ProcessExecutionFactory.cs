// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Creates process executions backed by real OS processes.
/// </summary>
internal sealed class ProcessExecutionFactory(
    ILogger<ProcessExecutionFactory> logger) : IProcessExecutionFactory
{
    public IProcessExecution CreateExecution(string fileName, string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
    {
        var suppressLogging = options.SuppressLogging;

        if (!suppressLogging)
        {
            logger.LogDebug("Running {FullName} with args: {Args}", workingDirectory.FullName, string.Join(" ", args));

            if (env is not null)
            {
                foreach (var envKvp in env)
                {
                    logger.LogDebug("Running {FullName} with env: {EnvKey}={EnvValue}", workingDirectory.FullName, envKvp.Key, envKvp.Value);
                }
            }
        }

        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory.FullName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (env is not null)
        {
            foreach (var envKvp in env)
            {
                startInfo.EnvironmentVariables[envKvp.Key] = envKvp.Value;
            }
        }

        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }

        var process = new Process { StartInfo = startInfo };
        return new ProcessExecution(process, logger, options);
    }
}
