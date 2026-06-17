// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
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

        if (options.IsolateConsole)
        {
            return CreateIsolatedExecution(fileName, args, env, workingDirectory, options, effectiveLogger);
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

    private static IProcessExecution CreateIsolatedExecution(
        string fileName,
        string[] args,
        IDictionary<string, string>? env,
        DirectoryInfo workingDirectory,
        ProcessInvocationOptions options,
        ILogger logger)
    {
        // Fail fast on Windows + IsolateConsole without a job handle. Silently falling through
        // would defeat the kill-on-close safety net that isolation is supposed to enable —
        // exactly the same defense-in-depth check IsolatedConsoleSpawner already enforces.
        if (OperatingSystem.IsWindows() && options.ConsoleProcessJob is null)
        {
            // Use a string literal instead of nameof(options.ConsoleProcessJob) so the analyzer
            // is satisfied (CA2208 rejects a property path; the actual paramName here describes
            // the missing option, not a method parameter).
            throw new ArgumentNullException(
                "options.ConsoleProcessJob",
                "ConsoleProcessJob is required on Windows when IsolateConsole is true. Pass the DI-registered WindowsConsoleProcessJob singleton.");
        }

        var startInfo = new IsolatedProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory.FullName,
            // Only Windows uses the job handle — the Unix partial of IsolatedProcess ignores
            // it because Unix process-group semantics + SIGTERM cover the orphan case.
            JobHandle = OperatingSystem.IsWindows() ? options.ConsoleProcessJob?.Handle : null,
        };

        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }

        if (env is not null)
        {
            foreach (var envKvp in env)
            {
                startInfo.Environment[envKvp.Key] = envKvp.Value;
            }
        }

        // Mutable line buffer the wrapper exposes via EnvironmentVariables. Captured here
        // (not lazily from IsolatedProcessStartInfo.Environment) so EnvironmentVariables stays
        // valid after the wrapper takes ownership of the IsolatedProcess.
        var envSnapshot = startInfo.HasCustomEnvironment
            ? new Dictionary<string, string?>(startInfo.Environment, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var argsSnapshot = startInfo.ArgumentList.ToArray();

        // Per-line callbacks fan out to the same callback shape ProcessExecution exposes:
        // log trace + StandardOutputCallback / StandardErrorCallback. Mirrors
        // ProcessExecution.ForwardStreamToLoggerAsync so the two paths produce identical
        // log shape and external observers can't tell them apart.
        var isolated = IsolatedProcess.Start(
            startInfo,
            (sender, line) =>
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("{FileName}({ProcessId}) stdout: {Line}", fileName, sender.Id, line);
                }
                options.StandardOutputCallback?.Invoke(line);
            },
            (sender, line) =>
            {
                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace("{FileName}({ProcessId}) stderr: {Line}", fileName, sender.Id, line);
                }
                options.StandardErrorCallback?.Invoke(line);
            });

        return new IsolatedProcessExecution(
            isolated,
            fileName,
            argsSnapshot,
            envSnapshot,
            logger,
            options);
    }
}
