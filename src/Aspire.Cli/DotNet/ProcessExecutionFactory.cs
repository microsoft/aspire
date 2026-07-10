// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Creates process executions backed by real OS processes.
/// </summary>
internal sealed class ProcessExecutionFactory : IProcessExecutionFactory
{
    private readonly IEnvironment _environment;
    private readonly ILogger<ProcessExecutionFactory> _logger;
    private readonly ILayoutDiscovery? _layoutDiscovery;
    private readonly IBundleService? _bundleService;
    private readonly CliExecutionContext? _executionContext;

    public ProcessExecutionFactory(IEnvironment environment, ILogger<ProcessExecutionFactory> logger)
        : this(environment, logger, layoutDiscovery: null, bundleService: null, executionContext: null)
    {
    }

    public ProcessExecutionFactory(
        IEnvironment environment,
        ILogger<ProcessExecutionFactory> logger,
        ILayoutDiscovery? layoutDiscovery,
        IBundleService? bundleService,
        CliExecutionContext? executionContext)
    {
        _environment = environment;
        _logger = logger;
        _layoutDiscovery = layoutDiscovery;
        _bundleService = bundleService;
        _executionContext = executionContext;
    }

    public IProcessExecution CreateExecution(string fileName, string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, ProcessInvocationOptions options)
    {
        var effectiveLogger = options.SuppressLogging ? (ILogger)NullLogger.Instance : _logger;

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
            KillOnParentExit = options.KillOnParentExit,
        };

        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }

        // Touching Environment here snapshots the parent env on first access, so the strip below
        // applies even when the caller passes no explicit env. Then overlay the caller's env deltas.
        StripIdentityEnvVars(startInfo);
        ApplyEnvironmentVariableFilter(startInfo, options.EnvironmentVariableFilter);

        if (env is not null)
        {
            foreach (var envKvp in env)
            {
                startInfo.Environment[envKvp.Key] = envKvp.Value;
            }
        }

        return Build(startInfo, fileName, effectiveLogger, options, _environment, _layoutDiscovery, _bundleService, _executionContext);
    }

    public IProcessExecution CreateExecution(System.Diagnostics.ProcessStartInfo startInfo, ProcessInvocationOptions options)
    {
        var effectiveLogger = options.SuppressLogging ? (ILogger)NullLogger.Instance : _logger;

        effectiveLogger.LogDebug("Running {FileName} in {WorkingDirectory} with args: {Args}", startInfo.FileName, startInfo.WorkingDirectory, string.Join(" ", startInfo.ArgumentList));

        var isolatedStartInfo = new IsolatedProcessStartInfo
        {
            FileName = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            IsolateConsole = options.IsolateConsole,
            KillOnParentExit = options.KillOnParentExit,
        };

        foreach (var arg in startInfo.ArgumentList)
        {
            isolatedStartInfo.ArgumentList.Add(arg);
        }

        // Replace (not overlay) the env block so callers that did startInfo.Environment.Remove(key)
        // see that removal honored — e.g. PrebuiltAppHostServer.CreateStartInfo explicitly removes
        // KnownConfigNames.IntegrationLibsPath / IntegrationProbeManifestPath when they aren't
        // configured, to suppress any value the parent CLI happens to have set in its own env.
        // ProcessStartInfo.Environment is eagerly snapshotted from the parent, so iterating it gives
        // the authoritative "what the child should see" view; a missing key really means "do not pass
        // this to the child". Start from an empty block (UseEmptyEnvironment skips the parent snapshot
        // we would otherwise allocate and immediately discard) so HasCustomEnvironment is set and the
        // spawn uses our explicit block rather than re-inheriting the parent.
        var childEnvironment = isolatedStartInfo.UseEmptyEnvironment();
        foreach (var (key, value) in startInfo.Environment)
        {
            // Match ProcessStartInfo.Environment semantics: a null value means "do not set this
            // variable in the child" — we get there by simply not adding it.
            if (value is not null)
            {
                childEnvironment[key] = value;
            }
        }

        // Strip after the copy so an ASPIRE_CLI_* var the parent happens to hold is not re-introduced
        // into the child via startInfo.Environment (which is parent-seeded). Same rationale as the
        // other overload — see StripIdentityEnvVars.
        StripIdentityEnvVars(isolatedStartInfo);
        ApplyEnvironmentVariableFilter(isolatedStartInfo, options.EnvironmentVariableFilter);

        return Build(isolatedStartInfo, startInfo.FileName, effectiveLogger, options, _environment, _layoutDiscovery, _bundleService, _executionContext);
    }

    // Strip ASPIRE_CLI_* identity overrides from every spawned process — both the isolated AppHost
    // run path and every non-isolated subprocess. These env vars are an in-process, parent-only test
    // affordance: a developer or test bench uses them to coerce the *current* CLI into pretending it
    // is a different channel/version/commit or to retarget its emitted nuget.config at a local proxy.
    // Letting them leak into child processes (apphost, dotnet, restore, peer probes) means any nested
    // `aspire` invocation inherits the parent's lie about its identity, which silently corrupts
    // `aspire doctor`, breaks peer probing, and undermines the "what is this binary actually" answer
    // we want callers to see on disk. We strip before merging caller env so a caller can still re-add
    // an ASPIRE_CLI_* var deliberately if a future test needs to. See docs/specs/cli-identity-sidecar.md.
    private static void StripIdentityEnvVars(IsolatedProcessStartInfo startInfo)
    {
        foreach (var envVarName in Acquisition.IdentityResolver.IdentityEnvVarNames)
        {
            startInfo.Environment.Remove(envVarName);
        }
    }

    private static void ApplyEnvironmentVariableFilter(IsolatedProcessStartInfo startInfo, Func<string, bool>? environmentVariableFilter)
    {
        if (environmentVariableFilter is null)
        {
            return;
        }

        var keysToRemove = new List<string>();
        foreach (var key in startInfo.Environment.Keys)
        {
            if (environmentVariableFilter(key))
            {
                keysToRemove.Add(key);
            }
        }

        foreach (var key in keysToRemove)
        {
            startInfo.Environment.Remove(key);
        }
    }

    private static IProcessExecution Build(
        IsolatedProcessStartInfo startInfo,
        string fileName,
        ILogger logger,
        ProcessInvocationOptions options,
        IEnvironment environment,
        ILayoutDiscovery? layoutDiscovery,
        IBundleService? bundleService,
        CliExecutionContext? executionContext)
    {
        // Snapshot args + env now so the IProcessExecution surfaces them before StartAsync() spawns the
        // child. The extension-host launch path reads Arguments / EnvironmentVariables and returns
        // without ever calling StartAsync (DotNetCliRunner), so these must be valid pre-spawn.
        var argsSnapshot = startInfo.ArgumentList.ToArray();
        var envSnapshot = new Dictionary<string, string?>(startInfo.Environment, StringComparer.OrdinalIgnoreCase);

        if (options.Detached)
        {
            if (layoutDiscovery is null || bundleService is null || executionContext is null)
            {
                throw new InvalidOperationException("Detached process launch requires Aspire layout services.");
            }

            return new DetachedProcessExecution(
                startInfo,
                fileName,
                argsSnapshot,
                envSnapshot,
                logger,
                options,
                layoutDiscovery,
                bundleService,
                executionContext);
        }

        return new ProcessExecution(startInfo, fileName, argsSnapshot, envSnapshot, logger, options, environment);
    }
}
