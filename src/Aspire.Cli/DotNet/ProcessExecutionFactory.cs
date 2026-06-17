// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Creates process executions backed by real OS processes.
/// </summary>
internal sealed class ProcessExecutionFactory(
    ILogger<ProcessExecutionFactory> logger) : IProcessExecutionFactory
{
    public IProcessExecution CreateExecution(string fileName, string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
    {
        var effectiveLogger = options.SuppressLogging ? (ILogger)NullLogger.Instance : logger;

        effectiveLogger.LogDebug("Running {FileName} in {WorkingDirectory} with args: {Args}", fileName, workingDirectory.FullName, string.Join(" ", args));

        if (env is not null)
        {
            foreach (var envKvp in env)
            {
                effectiveLogger.LogDebug("{FileName} env: {EnvKey}={EnvValue}", fileName, envKvp.Key, envKvp.Value);
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

        // Strip ASPIRE_CLI_* identity overrides from every spawned process.
        // These env vars are an in-process, parent-only test affordance — a
        // developer or test bench uses them to coerce the *current* CLI into
        // pretending it is a different channel/version/commit or to retarget
        // its emitted nuget.config at a local proxy. Letting them leak into
        // child processes (apphost, dotnet, restore, peer probes) means any
        // nested `aspire` invocation inherits the parent's lie about its
        // identity, which silently corrupts `aspire doctor`, breaks peer
        // probing, and undermines the "what is this binary actually" answer
        // we want callers to see on disk. We strip before the explicit `env`
        // dictionary is merged so a caller can still re-add an ASPIRE_CLI_*
        // var deliberately if a future test needs to.
        // See docs/specs/cli-identity-sidecar.md.
        foreach (var envVarName in Acquisition.IdentityResolver.IdentityEnvVarNames)
        {
            startInfo.EnvironmentVariables.Remove(envVarName);
        }

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
        return new ProcessExecution(process, effectiveLogger, options);
    }
}
