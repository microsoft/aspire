// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
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
internal interface IIntegrationClosureRestorer
{
    /// <summary>
    /// Builds the CLI-managed AppHost's integration module project and emits the closure layout
    /// (project libs + probe manifest) under <c>.aspire/integrations/apphosts/&lt;hash&gt;/</c>.
    /// Returns <see langword="true"/> when generation/build succeeded, or <see langword="false"/>
    /// when the AppHost is not a CLI-managed file-based AppHost.
    /// </summary>
    Task<bool> RestoreAsync(FileInfo appHostFile, IntegrationClosureRestoreOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the integration closure layout for the given CLI-managed AppHost when a probe
    /// manifest has already been materialized by a prior <see cref="RestoreAsync"/> call. Used by
    /// run-time wiring to attach the probe manifest without forcing a rebuild.
    /// </summary>
    IntegrationClosureLayout? TryLoad(FileInfo appHostFile);
}

internal sealed record IntegrationClosureRestoreOptions(bool BuildModule = true, string? PackageSourceOverride = null)
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

internal sealed record IntegrationClosureLayout(
    DirectoryInfo WorkingDirectory,
    string? ProbeManifestPath,
    string? IntegrationLibsPath);

internal sealed class IntegrationClosureRestorer(
    ICSharpCliManagedAppHostModuleGenerator moduleGenerator,
    IDotNetCliRunner dotNetCliRunner,
    ILogger<IntegrationClosureRestorer> logger) : IIntegrationClosureRestorer
{
    // Closure file names + project-file XML emission live on IntegrationClosureBuilder so they
    // stay in lock-step with PrebuiltAppHostServer (the polyglot path emits the same files and
    // we'd silently miss entries if these drifted).
    internal const string IntegrationRestoreFolderName = IntegrationClosureBuilder.IntegrationRestoreFolderName;
    internal const string ClosureMetadataFileName = IntegrationClosureBuilder.ClosureMetadataFileName;
    internal const string ClosureSourcesFileName = IntegrationClosureBuilder.ClosureSourcesFileName;
    internal const string ClosureTargetsFileName = IntegrationClosureBuilder.ClosureTargetsFileName;
    internal const string ProjectRefAssemblyNamesFileName = IntegrationClosureBuilder.ProjectRefAssemblyNamesFileName;

    public async Task<bool> RestoreAsync(FileInfo appHostFile, IntegrationClosureRestoreOptions options, CancellationToken cancellationToken)
    {
        // Honor the caller-supplied PackageSourceOverride during module regeneration so that
        // workflows like `aspire add --source <feed>` keep their additional feed wired into the
        // generated module project. Without this, the regenerated project would lose the
        // override's RestoreAdditionalProjectSources / RestoreConfigFile entry and the
        // subsequent build would fail when the package is only resolvable via the override.
        FileInfo? moduleProjectFile;
        if (options.PackageSourceOverride is { Length: > 0 } packageSourceOverride)
        {
            var appHostDirectory = appHostFile.Directory
                ?? throw new InvalidOperationException($"AppHost file '{appHostFile.FullName}' has no parent directory.");
            var configDirectory = ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);
            var config = AspireConfigFile.Load(configDirectory.FullName) ?? new AspireConfigFile();
            moduleProjectFile = await moduleGenerator
                .TryGenerateAsync(appHostFile, config, configDirectory, packageSourceOverride, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            moduleProjectFile = await moduleGenerator.TryGenerateAsync(appHostFile, cancellationToken).ConfigureAwait(false);
        }

        if (moduleProjectFile is null)
        {
            return false;
        }

        return await RestoreCoreAsync(appHostFile, moduleProjectFile, options, cancellationToken).ConfigureAwait(false);
    }

    public IntegrationClosureLayout? TryLoad(FileInfo appHostFile)
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

        return new IntegrationClosureLayout(
            workingDirectory,
            manifestExists ? probeManifestPath : null,
            integrationLibsPath);
    }

    private async Task<bool> RestoreCoreAsync(
        FileInfo appHostFile,
        FileInfo moduleProjectFile,
        IntegrationClosureRestoreOptions options,
        CancellationToken cancellationToken)
    {
        var workingDirectory = GetOrCreateWorkingDirectory(appHostFile);
        var restoreDir = Path.Combine(workingDirectory.FullName, IntegrationRestoreFolderName);
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

            CSharpCliManagedAppHostModuleGenerator.AddBuildProperty(buildOptions);
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

        var closureManifest = await ReadClosureManifestAsync(restoreDir, moduleProjectFile, cancellationToken).ConfigureAwait(false);
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

    private async Task<AppHostServerClosureManifest?> ReadClosureManifestAsync(string restoreDir, FileInfo moduleProjectFile, CancellationToken cancellationToken)
    {
        // The module project's obj/ directory holds project.assets.json (NuGet writes it under
        // BaseIntermediateOutputPath, not under the closure restoreDir). We compute it relative to
        // the module project file to keep working regardless of any future restoreDir layout
        // changes.
        var assetsFilePath = Path.Combine(moduleProjectFile.Directory!.FullName, "obj", IntegrationClosureBuilder.ProjectAssetsFileName);

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
