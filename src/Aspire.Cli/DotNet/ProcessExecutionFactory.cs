// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Creates process executions backed by real OS processes.
/// </summary>
internal sealed class ProcessExecutionFactory : IProcessExecutionFactory
{
    internal static IReadOnlyList<string> InvocationScopedEnvVarNames { get; } =
    [
        KnownConfigNames.CliAppHostSelectionOrigin
    ];

    // Strip ASPIRE_CLI_* identity overrides from every spawned process — both the isolated AppHost
    // run path and every non-isolated subprocess. These env vars are an in-process, parent-only test
    // affordance: a developer or test bench uses them to coerce the *current* CLI into pretending it
    // is a different channel/version/commit or to retarget its emitted nuget.config at a local proxy.
    // Letting them leak into child processes (apphost, dotnet, restore, peer probes) means any nested
    // `aspire` invocation inherits the parent's lie about its identity, which silently corrupts
    // `aspire doctor`, breaks peer probing, and undermines the "what is this binary actually" answer
    // we want callers to see on disk. See docs/specs/cli-identity-sidecar.md.
    //
    // Invocation-scoped values are stripped too so AppHost and build children cannot inherit them.
    // In both cases callers can still deliberately provide a value (e.g. to a detached child CLI).
    //
    // Case-insensitive because ProcessStartInfo.Environment is case-sensitive on Unix, while the child
    // CLI reads these through case-insensitive configuration, so no differently-cased copy may survive.
    private static readonly HashSet<string> s_strippedEnvVarNames = new(
        [.. Acquisition.IdentityResolver.IdentityEnvVarNames, .. InvocationScopedEnvVarNames],
        StringComparer.OrdinalIgnoreCase);

    private readonly IEnvironment _environment;
    private readonly ILogger<ProcessExecutionFactory> _logger;

    public ProcessExecutionFactory(IEnvironment environment, ILogger<ProcessExecutionFactory> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public IProcessExecution CreateExecution(string fileName, string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
    {
        var effectiveLogger = options.SuppressLogging ? (ILogger)NullLogger.Instance : _logger;

        // `dotnet run --project AppHost.csproj -- <appHostArgs>` reaches this factory with the
        // forwarded AppHost arguments still attached, so redact past the separator before logging.
        // Direct AppHost launches have no separator at all and instead declare the boundary through
        // ProcessInvocationOptions.AppHostArgumentStartIndex.
        var loggableArgs = options.AppHostArgumentStartIndex is { } appHostArgumentStartIndex
            ? AppHostArgumentRedactor.RedactFromToString(args, appHostArgumentStartIndex)
            : AppHostArgumentRedactor.RedactToString(args);
        effectiveLogger.LogDebug("Running {FileName} in {WorkingDirectory} with args: {Args}", fileName, workingDirectory.FullName, loggableArgs);

        if (env is not null)
        {
            foreach (var envKvp in env)
            {
                effectiveLogger.LogDebug("{FileName} env: {EnvKey}={EnvValue}", fileName, envKvp.Key, envKvp.Value);
            }
        }

        var startInfo = CreateProcessStartInfo(fileName, args, workingDirectory.FullName, options);

        // Touching Environment snapshots the parent env. Strip before overlaying explicit values so
        // callers can still deliberately provide a stripped value (e.g. to a detached child CLI).
        RemoveInheritedEnvironmentVariables(startInfo.Environment, options.EnvironmentVariableFilter);

        if (env is not null)
        {
            foreach (var envKvp in env)
            {
                startInfo.Environment[envKvp.Key] = envKvp.Value;
            }
        }

        return new ProcessExecution(startInfo, effectiveLogger, options, _environment);
    }

    public IProcessExecution CreateExecution(ProcessStartInfo startInfo, ProcessInvocationOptions options)
    {
        var effectiveLogger = options.SuppressLogging ? (ILogger)NullLogger.Instance : _logger;

        // Same redaction boundary as the ArgumentList-building overload: anything after the first
        // "--" is application input forwarded to the AppHost and must not be logged verbatim.
        effectiveLogger.LogDebug("Running {FileName} in {WorkingDirectory} with args: {Args}", startInfo.FileName, startInfo.WorkingDirectory, AppHostArgumentRedactor.RedactToString(startInfo.ArgumentList));

        // Only the caller's command line and environment are used; the launch mode (stdio, console,
        // job, detach) always comes from the options so every child is spawned the same way.
        var childStartInfo = CreateProcessStartInfo(startInfo.FileName, startInfo.ArgumentList, startInfo.WorkingDirectory, options);

        // Replace (not overlay) the env block so callers that did startInfo.Environment.Remove(key)
        // see that removal honored — e.g. PrebuiltAppHostServer.CreateStartInfo explicitly removes
        // KnownConfigNames.IntegrationLibsPath / IntegrationProbeManifestPath when they aren't
        // configured, to suppress any value the parent CLI happens to have set in its own env.
        // The caller's ProcessStartInfo.Environment is seeded from the parent, so it already is the
        // authoritative "what the child should see" view; a missing key means "do not pass it".
        childStartInfo.Environment.Clear();
        foreach (var (key, value) in startInfo.Environment)
        {
            // A null value means "do not set this variable in the child".
            if (value is not null)
            {
                childStartInfo.Environment[key] = value;
            }
        }

        // Strip after the copy so an ASPIRE_CLI_* var the parent happens to hold is not re-introduced
        // through the caller's parent-seeded environment.
        RemoveInheritedEnvironmentVariables(childStartInfo.Environment, options.EnvironmentVariableFilter);

        return new ProcessExecution(childStartInfo, effectiveLogger, options, _environment);
    }

    /// <summary>
    /// Maps the launch options onto a <see cref="ProcessStartInfo"/>. Standard handles are assigned by
    /// <see cref="ProcessExecution.StartAsync"/> so an execution that is never started holds no handles.
    /// </summary>
    internal static ProcessStartInfo CreateProcessStartInfo(string fileName, IEnumerable<string> arguments, string workingDirectory, ProcessInvocationOptions options)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
        };

        if (!options.Detached)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            // Pin encodings so process output decoding is stable regardless of the ambient
            // Console.OutputEncoding (e.g. on container hosts that leave it set to ASCII).
            startInfo.StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
            startInfo.StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        }
        else if (!OperatingSystem.IsWindows())
        {
            // setsid(): a new session and process group, so the child outlives cleanup of the
            // launcher's process group/session and terminal hangup (#18484). It stays a direct child,
            // so its exit code is observable while the CLI is alive. Not used on Windows, where it
            // means DETACHED_PROCESS: without a console, `aspire stop` could not deliver CTRL+C.
            startInfo.StartDetached = true;
        }

        if (OperatingSystem.IsWindows() && (options.IsolateConsole || options.KillOnParentExit || options.Detached))
        {
            // CreateNoWindow (CREATE_NO_WINDOW) still allocates a new console for the child, just
            // without a visible window; unlike DETACHED_PROCESS, the child is attached to a console.
            // That lets DCP stop-process-tree AttachConsole to it and send CTRL_C_EVENT without also
            // signalling the CLI (covered by ProcessExecutionTests). Children that only need
            // parent-exit protection or detachment keep sharing the CLI's console.
            startInfo.CreateNoWindow = options.IsolateConsole;
            // KillOnParentExit assigns the child to a kill-on-close job atomically at creation. The
            // runtime's job also sets JOB_OBJECT_LIMIT_BREAKAWAY_OK, so DCP can outlive the CLI to
            // finish cleanup by spawning itself with CREATE_BREAKAWAY_FROM_JOB, provided no nested
            // job forbids breakaway (dotnet run's does, so DotNetAppHostProject opts out for it).
            // Unix children rely on the cooperative parent-liveness watchdog instead (see LayoutProcessRunner).
            startInfo.KillOnParentExit = options.KillOnParentExit;
            // Long-lived children must not keep unrelated inheritable CLI handles (sockets, other
            // children's pipes) open, so inherit only the standard handles.
            startInfo.InheritedHandles = [];
        }

        return startInfo;
    }

    private static void RemoveInheritedEnvironmentVariables(IDictionary<string, string?> environment, Func<string, bool>? environmentVariableFilter)
    {
        foreach (var key in environment.Keys.ToArray())
        {
            if (s_strippedEnvVarNames.Contains(key) || environmentVariableFilter?.Invoke(key) == true)
            {
                environment.Remove(key);
            }
        }
    }
}
