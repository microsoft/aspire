// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;
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

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory.FullName,
            IsolateConsole = options.IsolateConsole,
            // Only the isolated path on Windows uses the kill-on-close job; the non-isolated path
            // and every Unix path leave it null. The job is the process-wide singleton, created on
            // demand the first time an isolated child needs it.
            JobHandle = options.IsolateConsole && OperatingSystem.IsWindows() ? WindowsConsoleProcessJob.Shared.Handle : null,
        };

        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }

        // Strip ASPIRE_CLI_* identity overrides from every spawned process — both the isolated
        // AppHost run path and every non-isolated subprocess. These env vars are an in-process,
        // parent-only test affordance: a developer or test bench uses them to coerce the *current*
        // CLI into pretending it is a different channel/version/commit or to retarget its emitted
        // nuget.config at a local proxy. Letting them leak into child processes (apphost, dotnet,
        // restore, peer probes) means any nested `aspire` invocation inherits the parent's lie
        // about its identity, which silently corrupts `aspire doctor`, breaks peer probing, and
        // undermines the "what is this binary actually" answer we want callers to see on disk.
        // Touching Environment here (which snapshots the parent env on first access) also forces
        // the spawn to build a custom env block, so the strip applies even when the caller passes
        // no explicit `env`. We strip before merging `env` so a caller can still re-add an
        // ASPIRE_CLI_* var deliberately if a future test needs to.
        // See docs/specs/cli-identity-sidecar.md.
        foreach (var envVarName in Acquisition.IdentityResolver.IdentityEnvVarNames)
        {
            startInfo.Environment.Remove(envVarName);
        }

        if (env is not null)
        {
            foreach (var envKvp in env)
            {
                startInfo.Environment[envKvp.Key] = envKvp.Value;
            }
        }

        // Snapshot args + env now so the IProcessExecution surfaces them before Start() spawns the
        // child. The extension-host launch path reads Arguments / EnvironmentVariables and returns
        // without ever calling Start (DotNetCliRunner), so these must be valid pre-spawn.
        var argsSnapshot = startInfo.ArgumentList.ToArray();
        var envSnapshot = new Dictionary<string, string?>(startInfo.Environment, StringComparer.OrdinalIgnoreCase);

        return new ProcessExecution(startInfo, fileName, argsSnapshot, envSnapshot, effectiveLogger, options);
    }
}
