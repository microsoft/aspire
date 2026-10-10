// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

// Explicit development override for the NativeHosting exploration, not a
// replacement for the managed AppHost server or its integration package loader.
internal sealed class NativeAppHostServerProject : IAppHostServerProject, IDisposable
{
    internal const string ExecutableEnvironmentVariable = "ASPIRE_CLI_NATIVE_APPHOST_SERVER";
    private readonly string _executable;
    private readonly IProcessExecutionFactory _processes;
    private readonly ILogger _logger;
    private DirectoryInfo? _directory;

    public NativeAppHostServerProject(string appPath, string executable, IProcessExecutionFactory processes, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The native AppHost exploration currently supports Unix sockets only.");
        }

        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new ArgumentException($"{ExecutableEnvironmentVariable} must name an existing absolute executable path.");
        }

        AppDirectoryPath = appPath;
        _executable = executable;
        _processes = processes;
        _logger = logger;
    }

    public string AppDirectoryPath { get; }
    public string GetInstanceIdentifier() => AppDirectoryPath;

    public Task<AppHostServerPrepareResult> PrepareAsync(
        string sdkVersion,
        IEnumerable<IntegrationReference> integrations,
        string? requestedChannel = null,
        string? packageSourceOverride = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // GuestAppHostProject always supplies Hosting and the language's codegen
        // package. They are CLI metadata here, not assemblies to restore or load.
        if (integrations.Any(reference => reference.Source != IntegrationSource.Nuget ||
            reference.Name is not ("Aspire.Hosting" or "Aspire.Hosting.CodeGeneration.TypeScript")))
        {
            throw new NotSupportedException("The native exploration accepts only its offline TypeScript SDK and explicitly configured integration host, not managed or npm integration packages.");
        }

        return Task.FromResult(new AppHostServerPrepareResult(true, null, NeedsCodeGeneration: true));
    }

    public async Task<AppHostServerRunResult> RunAsync(
        int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables,
        string[]? additionalArgs,
        bool debug,
        AppHostServerRunControl? runControl)
    {
        if (additionalArgs is { Length: > 0 })
        {
            throw new NotSupportedException("The native AppHost exploration supports run, not managed server command-line modes.");
        }

        var startInfo = new ProcessStartInfo(_executable) { WorkingDirectory = AppDirectoryPath, UseShellExecute = false };
        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        _directory ??= Directory.CreateTempSubdirectory("aspire-native-");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var socketPath = Path.Combine(_directory.FullName, "apphost.sock");
        startInfo.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath;
        startInfo.Environment["NATIVE_HOSTING_CLI"] = "1";
        var output = new OutputCollector();
        var execution = _processes.CreateExecution(startInfo, new ProcessInvocationOptions
        {
            StandardOutputCallback = line => { output.AppendOutput(line); _logger.LogDebug("Native AppHost stdout: {Line}", line); },
            StandardErrorCallback = line => { output.AppendError(line); _logger.LogDebug("Native AppHost stderr: {Line}", line); },
            Lifetime = ChildProcessLifetime.AppHost,
            IsolateConsole = runControl?.IsolateConsole ?? false,
            KillOnParentExit = runControl?.KillOnParentExit ?? false,
            GracefulShutdownSignaler = runControl?.GracefulShutdownSignaler,
            ShutdownService = runControl?.ShutdownService,
            KillEntireProcessTreeOnCancel = true
        });
        try
        {
            await execution.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await execution.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new AppHostServerRunResult(socketPath, output, execution);
    }

    public void Dispose()
    {
        _directory?.Delete(recursive: true);
        _directory = null;
    }
}
