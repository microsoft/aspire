// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Restores the integration closure for CLI-managed C# AppHosts using the same closure-manifest
/// pattern that <see cref="PrebuiltAppHostServer"/> uses for the polyglot/prebuilt AppHost server.
/// The integration project is built, its transitive copy-local closure is captured into
/// <c>.aspire/integrations/apphosts/&lt;hash&gt;/</c>, and an
/// <see cref="IntegrationPackageProbeManifest"/> is emitted so the running AppHost can probe
/// integration assemblies from the cache instead of relying on bin output.
/// </summary>
internal sealed record CliManagedAppHostIntegrationClosureRestoreOptions(bool BuildModule = true)
{
    /// <summary>
    /// Optional caller-supplied invocation options to merge into the build of the integration
    /// module. Used by callers that need process options beyond output capture.
    /// </summary>
    public ProcessInvocationOptions? BuildInvocationOptions { get; init; }

    /// <summary>
    /// Optional caller-owned output collector for capturing the integration module build output.
    /// </summary>
    public OutputCollector? BuildOutputCollector { get; init; }
}

internal sealed record CliManagedAppHostIntegrationClosureLayout(
    DirectoryInfo WorkingDirectory,
    string? ProbeManifestPath,
    string? IntegrationLibsPath);

internal sealed class CliManagedAppHostIntegrationClosureRestorer(
    IDotNetCliRunner dotNetCliRunner,
    ILogger<CliManagedAppHostIntegrationClosureRestorer> logger)
{
    /// <summary>
    /// Builds the CLI-managed AppHost's integration module project and emits the closure layout
    /// (project libs + probe manifest) under <c>.aspire/integrations/apphosts/&lt;hash&gt;/</c>.
    /// Returns <see langword="true"/> when the module build and closure materialization succeeded.
    /// </summary>
    public async Task<bool> RestoreAsync(FileInfo appHostFile, FileInfo moduleProjectFile, CliManagedAppHostIntegrationClosureRestoreOptions options, CancellationToken cancellationToken)
        => await RestoreCoreAsync(appHostFile, moduleProjectFile, options, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Returns the integration closure layout for the given CLI-managed AppHost when a probe
    /// manifest has already been materialized by a prior <see cref="RestoreAsync"/> call. Used by
    /// run-time wiring to attach the probe manifest without forcing a rebuild.
    /// </summary>
    public static CliManagedAppHostIntegrationClosureLayout? TryLoad(FileInfo appHostFile)
    {
        var workingDirectory = TryGetWorkingDirectory(appHostFile);
        if (workingDirectory is null)
        {
            return null;
        }

        var probeManifestPath = Path.Combine(workingDirectory.FullName, IntegrationPackageProbeManifest.FileName);
        var integrationLibsPath = TryReadIntegrationLibsPathFromState(workingDirectory.FullName);
        var manifestExists = File.Exists(probeManifestPath);

        // Surface null when neither artifact is present so callers can decide to re-restore
        // instead of attempting to wire env vars pointing at missing files.
        if (!manifestExists && integrationLibsPath is null)
        {
            return null;
        }

        return new CliManagedAppHostIntegrationClosureLayout(
            workingDirectory,
            manifestExists ? probeManifestPath : null,
            integrationLibsPath);
    }

    private async Task<bool> RestoreCoreAsync(
        FileInfo appHostFile,
        FileInfo moduleProjectFile,
        CliManagedAppHostIntegrationClosureRestoreOptions options,
        CancellationToken cancellationToken)
    {
        var workingDirectory = GetOrCreateWorkingDirectory(appHostFile);
        var restoreDir = Path.Combine(workingDirectory.FullName, IntegrationClosureBuilder.IntegrationRestoreFolderName);
        Directory.CreateDirectory(restoreDir);

        if (options.BuildModule)
        {
            var buildOptions = options.BuildInvocationOptions ?? new ProcessInvocationOptions();
            if (options.BuildOutputCollector is { } buildOutputCollector)
            {
                var existingStandardOutputCallback = buildOptions.StandardOutputCallback;
                var existingStandardErrorCallback = buildOptions.StandardErrorCallback;

                buildOptions.StandardOutputCallback = line =>
                {
                    existingStandardOutputCallback?.Invoke(line);
                    buildOutputCollector.AppendOutput(line);
                };
                buildOptions.StandardErrorCallback = line =>
                {
                    existingStandardErrorCallback?.Invoke(line);
                    buildOutputCollector.AppendError(line);
                };
            }

            OutputCollector? localCollector = null;
            if (buildOptions.StandardOutputCallback is null && buildOptions.StandardErrorCallback is null)
            {
                localCollector = new OutputCollector();
                buildOptions.StandardOutputCallback = localCollector.AppendOutput;
                buildOptions.StandardErrorCallback = localCollector.AppendError;
            }

            var exitCode = await dotNetCliRunner.BuildAsync(moduleProjectFile, noRestore: false, buildOptions, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                if (localCollector is not null)
                {
                    var combined = string.Join(Environment.NewLine, localCollector.GetLines().Select(l => l.Line));
                    logger.LogError("Failed to build CLI-managed AppHost integration module. Output:\n{Output}", combined);
                }
                else
                {
                    logger.LogError("Failed to build CLI-managed AppHost integration module (exit code {ExitCode}).", exitCode);
                }
                return false;
            }
        }

        var closureManifest = await ReadClosureManifestAsync(restoreDir, cancellationToken).ConfigureAwait(false);
        if (closureManifest is null)
        {
            return false;
        }

        string? probeManifestPath = null;
        if (closureManifest.Entries.Any(static entry => entry.IsPackageBacked))
        {
            probeManifestPath = Path.Combine(workingDirectory.FullName, IntegrationPackageProbeManifest.FileName);
            await IntegrationPackageProbeManifest.WriteAsync(
                probeManifestPath,
                closureManifest.CreatePackageProbeManifest(),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var staleProbeManifestPath = Path.Combine(workingDirectory.FullName, IntegrationPackageProbeManifest.FileName);
            if (File.Exists(staleProbeManifestPath))
            {
                File.Delete(staleProbeManifestPath);
            }
        }

        string? integrationLibsPath = null;
        if (closureManifest.Entries.Any(static entry => !entry.IsPackageBacked))
        {
            var layoutStore = new AppHostServerProjectLayoutStore(workingDirectory.FullName, logger);
            var layout = await layoutStore.GetOrCreateAsync(closureManifest, cancellationToken).ConfigureAwait(false);
            if (layout is not null)
            {
                integrationLibsPath = layout.IntegrationLibsPath;
            }
        }

        // Persist the resolved libs path so TryLoad can reattach it on subsequent runs without
        // having to re-read the closure manifest.
        await PersistStateAsync(workingDirectory.FullName, integrationLibsPath, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<AppHostServerClosureManifest?> ReadClosureManifestAsync(string restoreDir, CancellationToken cancellationToken)
    {
        // The generated module's Directory.Build.props sets BaseIntermediateOutputPath under the
        // same integration-restore directory that receives the closure files, matching the
        // polyglot/prebuilt generated-project layout even though Aspire.csproj itself lives under
        // .aspire/modules so file-based AppHosts can reference it with #:project.
        var assetsFilePath = Path.Combine(restoreDir, "obj", IntegrationClosureBuilder.ProjectAssetsFileName);

        // The CLI-managed restorer treats missing closure files as a soft failure (log + return
        // null) so the caller surfaces "build did not emit closure" rather than crashing. We
        // pre-compute the appsettings content from project-ref assembly names because the CLI
        // path doesn't have the polyglot's IntegrationReference list available.
        var projectRefAssemblyNames = await IntegrationClosureBuilder.ReadProjectRefAssemblyNamesAsync(
            restoreDir, logger, cancellationToken).ConfigureAwait(false);
        var appSettings = CreateAppSettingsContent(projectRefAssemblyNames);

        return await IntegrationClosureBuilder.ReadClosureManifestAsync(
            restoreDir,
            assetsFilePath,
            appSettings,
            ClosureFileMissingBehavior.ReturnNull,
            logger,
            cancellationToken).ConfigureAwait(false);
    }

    private static string CreateAppSettingsContent(IReadOnlyList<string> projectRefAssemblyNames)
    {
        // appsettings.json content is hashed into the closure manifest as a cache-invalidation
        // signal; for the CLI-managed path we only contribute the project-ref assembly names
        // (package ids are already captured via closure metadata). The CLI-managed AppHost
        // itself doesn't consume this file today but keeping it stable keeps the cache layout
        // symmetric with PrebuiltAppHostServer.
        var atsAssemblies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "Aspire.Hosting" };
        foreach (var name in projectRefAssemblyNames)
        {
            atsAssemblies.Add(name);
        }

        var assembliesJson = string.Join(",\n      ", atsAssemblies.Select(static a => $"\"{a}\""));
        return $$"""
            {
              "AtsAssemblies": [
                {{assembliesJson}}
              ]
            }
            """;
    }

    // ─── Working-directory layout ────────────────────────────────────────────────

    /// <summary>
    /// Computes the per-AppHost cache directory under <c>.aspire/integrations/apphosts/</c>. The
    /// directory name is a stable 12-character hash of the AppHost directory path so different
    /// AppHosts in the same workspace get separate caches and so the same AppHost reuses its cache
    /// across CLI runs. Mirrors <see cref="PrebuiltAppHostServer"/>'s working-directory layout so
    /// the on-disk shape is identical between polyglot and CLI-managed C# modes.
    /// </summary>
    internal static DirectoryInfo? TryGetWorkingDirectory(FileInfo appHostFile)
    {
        var appHostDirectory = appHostFile.Directory;
        if (appHostDirectory is null)
        {
            return null;
        }

        return IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(appHostDirectory);
    }

    internal static DirectoryInfo GetOrCreateWorkingDirectory(FileInfo appHostFile)
    {
        var appHostDirectory = appHostFile.Directory
            ?? throw new InvalidOperationException($"AppHost file '{appHostFile.FullName}' has no parent directory.");

        var workingDirectory = IntegrationClosureBuilder.GetAppHostIntegrationCacheDirectory(appHostDirectory);
        Directory.CreateDirectory(workingDirectory.FullName);
        return workingDirectory;
    }

    private const string LibsPathStateFileName = "integration-libs-path.txt";

    private static async Task PersistStateAsync(string workingDirectory, string? integrationLibsPath, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(workingDirectory, LibsPathStateFileName);
        if (string.IsNullOrWhiteSpace(integrationLibsPath))
        {
            if (File.Exists(statePath))
            {
                File.Delete(statePath);
            }
            return;
        }

        await File.WriteAllTextAsync(statePath, integrationLibsPath, cancellationToken).ConfigureAwait(false);
    }

    private static string? TryReadIntegrationLibsPathFromState(string workingDirectory)
    {
        var statePath = Path.Combine(workingDirectory, LibsPathStateFileName);
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var value = File.ReadAllText(statePath).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
