// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Launches a guest language process by delegating to the VS Code extension debug session.
/// </summary>
internal sealed class ExtensionGuestLauncher : IGuestProcessLauncher
{
    private readonly IExtensionInteractionService _extensionInteractionService;
    private readonly FileInfo _appHostFile;
    private readonly bool _debug;

    public ExtensionGuestLauncher(
        IExtensionInteractionService extensionInteractionService,
        FileInfo appHostFile,
        bool debug)
    {
        _extensionInteractionService = extensionInteractionService;
        _appHostFile = appHostFile;
        _debug = debug;
    }

    public async Task<(int ExitCode, OutputCollector? Output)> LaunchAsync(
        string command,
        string[] args,
        DirectoryInfo workingDirectory,
        IDictionary<string, string> environmentVariables,
        Func<Task>? afterLaunchAsync,
        GuestLaunchOptions? options,
        CancellationToken cancellationToken)
    {
        // GuestLaunchOptions is intentionally ignored — the extension owns the debug-session
        // lifecycle and has its own teardown path (it does not spawn a child process here, so
        // there's nothing for the console-isolation / graceful-then-tree-kill ladder to target).
        // Prepend the runtime command (e.g., "npx") as the first argument so the
        // extension can extract it as the runtimeExecutable for the debug session.
        var allArgs = new List<string> { command };
        allArgs.AddRange(args);
        var effectiveEnvironmentVariables = environmentVariables.ToDictionary();
        ProfilingTelemetry.AddActivityContextToEnvironment(Activity.Current, effectiveEnvironmentVariables);

        await _extensionInteractionService.LaunchAppHostAsync(
            _appHostFile.FullName,
            allArgs,
            effectiveEnvironmentVariables.Select(kvp => new EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList(),
            _debug);

        if (afterLaunchAsync is not null)
        {
            await afterLaunchAsync().ConfigureAwait(false);
        }

        return (0, null);
    }
}
