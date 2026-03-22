// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
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
        CancellationToken cancellationToken)
    {
        // Prepend the runtime command (e.g., "npx") as the first argument so the
        // extension can extract it as the runtimeExecutable for the debug session.
        var allArgs = new List<string> { command };
        allArgs.AddRange(args);

        await _extensionInteractionService.LaunchAppHostAsync(
            _appHostFile.FullName,
            allArgs,
            environmentVariables.Select(kvp => new EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList(),
            _debug);

        return (0, null);
    }
}
