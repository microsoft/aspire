// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Processes;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

// The opt-in server preserves the CLI's bootstrap and backchannel RPC contracts.
internal sealed class NativeAppHostServerProject : IAppHostServerProject, IDisposable
{
    internal const string ExecutableEnvironmentVariable = "ASPIRE_CLI_NATIVE_APPHOST_SERVER";
    internal const string OperationEnvironmentVariable = "ASPIRE_NATIVE_OPERATION";
    private readonly string _executable;
    private readonly IProcessExecutionFactory _processes;
    private readonly ILogger _logger;
    private DirectoryInfo? _directory;
    private string? _generatedSdkDirectory;
    private int _preparationCount;
    private readonly List<string> _socketPaths = [];

    public NativeAppHostServerProject(string appPath, string executable, IProcessExecutionFactory processes, ILogger logger)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new ArgumentException("AppHost Server v2 requires an existing absolute executable path.", nameof(executable));
        }
        AppDirectoryPath = appPath;
        _executable = executable;
        _processes = processes;
        _logger = logger;
    }

    public string AppDirectoryPath { get; }
    internal BundleLayoutLease? LayoutLease { get; init; }
    public string GetInstanceIdentifier() => AppDirectoryPath;

    internal static async Task<NativeAppHostServerProject> CreateAsync(string appPath, IBundleService bundleService,
        IEnvironment environment, IProcessExecutionFactory processes, ILogger logger, CancellationToken cancellationToken)
    {
        // Developer overrides still borrow bundle tooling for provider SDK preparation.
        var lease = await bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "apphost-server-v2", cancellationToken).ConfigureAwait(false);
        try
        {
            var path = environment.GetEnvironmentVariable(ExecutableEnvironmentVariable);
            if (string.IsNullOrEmpty(path))
            {
                path = lease?.Layout.GetAppHostServerPath();
                if (path is null || !File.Exists(path))
                {
                    throw new InvalidOperationException(
                        "This Aspire bundle does not contain AppHost Server v2. Update or reinstall the CLI, " +
                        "or disable features.appHostServerV2 in this AppHost's aspire.config.json.");
                }
            }

            return new(appPath, path, processes, logger) { LayoutLease = lease };
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    public async Task<AppHostServerPrepareResult> PrepareAsync(string sdkVersion, IEnumerable<IntegrationReference> integrations,
        string? requestedChannel = null, string? packageSourceOverride = null, string? packageSourceOverridePattern = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _generatedSdkDirectory = null;
        var references = integrations.ToArray();
        if (references.Any(reference => reference.Source is not IntegrationSource.Npm &&
            !(reference.Source is IntegrationSource.Nuget or IntegrationSource.Project &&
              (string.Equals(reference.Name, "Aspire.Hosting", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reference.Name, "Aspire.Hosting.CodeGeneration.TypeScript", StringComparison.OrdinalIgnoreCase)))))
        {
            throw new NotSupportedException("AppHost Server v2 requires protocol-v2 provider packages. Managed integration assemblies cannot be loaded into this server.");
        }

        var providers = new List<(NativeProviderDescriptor Descriptor, string EntryPoint)>();
        var packageNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references.Where(reference => reference.Source == IntegrationSource.Npm))
        {
            var entryPoint = reference.Path ?? throw new InvalidDataException($"Provider '{reference.Name}' has no entry point.");
            var root = Path.GetDirectoryName(entryPoint)!;
            var descriptorPath = Path.Combine(root, "aspire.apphost-server.json");
            if (!File.Exists(descriptorPath))
            {
                throw new NotSupportedException($"Provider '{reference.Name}' does not declare AppHost Server v2 support in aspire.apphost-server.json.");
            }
            var descriptor = NativeProviderDescriptor.Read(
                await File.ReadAllTextAsync(descriptorPath, cancellationToken).ConfigureAwait(false));
            if (!packageNames.Add(descriptor.PackageName))
            {
                throw new InvalidDataException($"Provider '{reference.Name}' declares a duplicate package identity.");
            }
            providers.Add((descriptor with
            {
                Metadata = ResolvePackageFile(root, descriptor.Metadata),
                SdkFiles = descriptor.SdkFiles.ToDictionary(file => file.Key,
                    file => ResolvePackageFile(root, file.Value), StringComparer.Ordinal)
            }, entryPoint));
        }
        if (providers.Count > 0)
        {
            var managed = LayoutLease?.Layout.GetManagedPath();
            var contract = Path.Combine(Path.GetDirectoryName(_executable)!, "ats-contract.json");
            if (managed is null || !File.Exists(managed) || !File.Exists(contract))
            {
                throw new InvalidOperationException("AppHost Server v2 provider SDK generation requires the matching Aspire bundle tooling and ATS contract.");
            }
            _directory ??= Directory.CreateTempSubdirectory("aspire-apphost-server-");
            var generatedSdkDirectory = Path.Combine(_directory.FullName, $"sdk-{++_preparationCount}");
            var requestPath = Path.Combine(_directory.FullName, "codegen-request.json");
            await File.WriteAllTextAsync(requestPath, new JsonObject
            {
                ["coreContract"] = contract,
                ["output"] = generatedSdkDirectory,
                ["providers"] = new JsonArray(providers.Select(provider => (JsonNode)new JsonObject
                {
                    ["packageName"] = provider.Descriptor.PackageName,
                    ["metadata"] = provider.Descriptor.Metadata,
                    ["sdkFiles"] = new JsonObject(provider.Descriptor.SdkFiles.Select(file =>
                        new KeyValuePair<string, JsonNode?>(file.Key, JsonValue.Create(file.Value)))),
                    ["exports"] = new JsonArray(provider.Descriptor.Exports.Select(module => (JsonNode?)JsonValue.Create(module)).ToArray())
                }).ToArray())
            }.ToJsonString(), cancellationToken).ConfigureAwait(false);
            var output = new OutputCollector();
            var start = new ProcessStartInfo(managed) { WorkingDirectory = AppDirectoryPath, UseShellExecute = false };
            start.ArgumentList.Add("apphost-codegen-v2");
            start.ArgumentList.Add(requestPath);
            LayoutLease!.AddEnvironment(start);
            await using var execution = _processes.CreateExecution(start, new ProcessInvocationOptions
            {
                StandardOutputCallback = output.AppendOutput,
                StandardErrorCallback = output.AppendError
            });
            await execution.StartAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = await execution.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return new AppHostServerPrepareResult(false, output);
            }
            var runtimeProviders = new JsonArray();
            foreach (var provider in providers)
            {
                runtimeProviders.Add((JsonNode)new JsonObject
                {
                    ["packageName"] = provider.Descriptor.PackageName,
                    ["executable"] = "node",
                    ["arguments"] = new JsonArray(provider.EntryPoint)
                });
            }
            await File.WriteAllTextAsync(Path.Combine(generatedSdkDirectory, "providers.json"),
                runtimeProviders.ToJsonString(), cancellationToken).ConfigureAwait(false);
            _generatedSdkDirectory = generatedSdkDirectory;
        }

        return new AppHostServerPrepareResult(true, null, NeedsCodeGeneration: true);
    }

    private static string ResolvePackageFile(string root, string relativePath)
    {
        var path = Path.GetFullPath(relativePath, root);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relativePath) || Path.IsPathRooted(relative) ||
            relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(path))
        {
            throw new InvalidDataException($"Provider asset '{relativePath}' must exist inside its package.");
        }

        return path;
    }

    public async Task<AppHostServerRunResult> RunAsync(int hostPid,
        IReadOnlyDictionary<string, string>? environmentVariables, string[]? additionalArgs, bool debug,
        AppHostServerRunControl? runControl)
    {
        if (additionalArgs is { Length: > 0 })
        {
            throw new NotSupportedException("AppHost Server v2 does not support additional server command-line arguments.");
        }
        if (debug)
        {
            throw new NotSupportedException("AppHost Server v2 does not support server debugging.");
        }
        var startInfo = new ProcessStartInfo(_executable) { WorkingDirectory = AppDirectoryPath, UseShellExecute = false };
        startInfo.ArgumentList.Add("server");
        startInfo.ArgumentList.Add("--contentRoot");
        startInfo.ArgumentList.Add(AppDirectoryPath);
        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }
        LayoutLease?.AddEnvironment(startInfo);
        // Only the current session selects the operation; do not inherit a parent server's mode.
        var operation = environmentVariables?.GetValueOrDefault(OperationEnvironmentVariable) ?? "run";
        startInfo.Environment[OperationEnvironmentVariable] = operation;
        var parentStarted = ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(hostPid);
        OrphanDetectionEnvironment.Apply(startInfo.Environment, hostPid, parentStarted,
            KnownConfigNames.RemoteAppHostProcessId, KnownConfigNames.RemoteAppHostProcessStarted);
        OrphanDetectionEnvironment.Apply(startInfo.Environment, hostPid, parentStarted,
            KnownConfigNames.CliProcessId, KnownConfigNames.CliProcessStarted);
        var bootstrap = environmentVariables?.GetValueOrDefault(KnownConfigNames.IntegrationHostBootstrap) == "true";
        var suppressRuntimeComponents = bootstrap || operation != "run";
        if (suppressRuntimeComponents)
        {
            // SDK bootstrap, publishing, and inspection must not start local workloads,
            // even when the caller or the ambient environment supplies runtime paths.
            startInfo.Environment.Remove("ASPIRE_NATIVE_DCP_PATH");
            startInfo.Environment.Remove("ASPIRE_DASHBOARD_PATH");
            startInfo.Environment.Remove("ASPIRE_NATIVE_DASHBOARD_PORT");
        }
        if (bootstrap)
        {
            startInfo.Environment.Remove("AppHost__FilePath");
            startInfo.Environment.Remove(KnownConfigNames.UnixSocketPath);
        }
        if (!suppressRuntimeComponents && LayoutLease?.Layout.GetDcpPath() is { } dcpDirectory)
        {
            startInfo.Environment["ASPIRE_NATIVE_DCP_PATH"] = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
        }
        if (!suppressRuntimeComponents && LayoutLease?.Layout.GetDashboardPath() is { } dashboardPath)
        {
            startInfo.Environment["ASPIRE_DASHBOARD_PATH"] = dashboardPath;
            startInfo.Environment["ASPIRE_NATIVE_DASHBOARD_PORT"] = "0";
        }
        startInfo.Environment.Remove("ASPIRE_APPHOST_SERVER_V2_SDK_DIRECTORY");
        if (_generatedSdkDirectory is not null)
        {
            startInfo.Environment["ASPIRE_APPHOST_SERVER_V2_SDK_DIRECTORY"] = _generatedSdkDirectory;
        }
        // TMPDIR can exceed the Unix socket path limit. Use the CLI's private
        // socket directory and a fresh name so stale bootstrap sockets are not reused.
        var socketPath = CliPathHelper.CreateGuestAppHostSocketPath("apphost.sock");
        if (!OperatingSystem.IsWindows())
        {
            _socketPaths.Add(socketPath);
        }
        startInfo.Environment["REMOTE_APP_HOST_SOCKET_PATH"] = socketPath;
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
        try
        {
            foreach (var path in _socketPaths)
            {
                Cleanup(() => File.Delete(path), path);
            }
            _socketPaths.Clear();
            if (_directory is { } directory)
            {
                Cleanup(() => directory.Delete(recursive: true), directory.FullName);
            }
            _directory = null;
        }
        finally
        {
            LayoutLease?.Dispose();
        }
    }

    private void Cleanup(Action delete, string path)
    {
        try
        {
            delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Failed to remove native AppHost server temporary path {Path}", path);
        }
    }
}
