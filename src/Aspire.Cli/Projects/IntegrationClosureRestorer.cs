// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
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
    /// Returns the resolved layout when generation/build succeeded, or <see langword="null"/>
    /// when the AppHost is not a CLI-managed file-based AppHost.
    /// </summary>
    Task<IntegrationClosureLayout?> RestoreAsync(FileInfo appHostFile, IntegrationClosureRestoreOptions options, CancellationToken cancellationToken);

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
    /// module. Used by callers like `aspire add` to attach output collectors so build failures
    /// surface in the command output.
    /// </summary>
    public ProcessInvocationOptions? BuildInvocationOptions { get; init; }
}

internal sealed record IntegrationClosureLayout(
    DirectoryInfo WorkingDirectory,
    string ProbeManifestPath,
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

    public async Task<IntegrationClosureLayout?> RestoreAsync(FileInfo appHostFile, IntegrationClosureRestoreOptions options, CancellationToken cancellationToken)
    {
        var moduleProjectFile = await moduleGenerator.TryGenerateAsync(appHostFile, cancellationToken).ConfigureAwait(false);
        if (moduleProjectFile is null)
        {
            return null;
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
        if (!File.Exists(probeManifestPath))
        {
            return null;
        }

        var integrationLibsPath = TryReadIntegrationLibsPathFromState(workingDirectory.FullName);
        return new IntegrationClosureLayout(workingDirectory, probeManifestPath, integrationLibsPath);
    }

    private async Task<IntegrationClosureLayout?> RestoreCoreAsync(
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
                return null;
            }
        }

        var closureManifest = await ReadClosureManifestAsync(restoreDir, moduleProjectFile, cancellationToken).ConfigureAwait(false);
        if (closureManifest is null)
        {
            return null;
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

        if (probeManifestPath is null && integrationLibsPath is null)
        {
            return new IntegrationClosureLayout(workingDirectory, Path.Combine(workingDirectory.FullName, IntegrationPackageProbeManifest.FileName), null);
        }

        return new IntegrationClosureLayout(
            workingDirectory,
            probeManifestPath ?? Path.Combine(workingDirectory.FullName, IntegrationPackageProbeManifest.FileName),
            integrationLibsPath);
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
    /// directory name is a 12-character SHA-256 prefix of the AppHost directory path so different
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

        return new DirectoryInfo(ComputeWorkingDirectoryPath(appHostDirectory));
    }

    internal static DirectoryInfo GetOrCreateWorkingDirectory(FileInfo appHostFile)
    {
        var appHostDirectory = appHostFile.Directory
            ?? throw new InvalidOperationException($"AppHost file '{appHostFile.FullName}' has no parent directory.");

        var path = ComputeWorkingDirectoryPath(appHostDirectory);
        Directory.CreateDirectory(path);
        return new DirectoryInfo(path);
    }

    internal static string ComputeWorkingDirectoryPath(DirectoryInfo appHostDirectory)
    {
        var integrationCacheDirectory = ConfigurationHelper.GetIntegrationCacheDirectory(appHostDirectory);
        var appHostFullPath = appHostDirectory.FullName;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(appHostFullPath));
        var hashFragment = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return Path.Combine(integrationCacheDirectory.FullName, "apphosts", hashFragment);
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

    // ─── Project-file closure target helpers ─────────────────────────────────────

    /// <summary>
    /// Convenience wrapper around <see cref="IntegrationClosureBuilder.AddClosureProperties"/>.
    /// Kept for the module generator's existing call site; new callers should prefer
    /// <see cref="IntegrationClosureBuilder"/> directly.
    /// </summary>
    public static void AddClosureProperties(XElement propertyGroup, string restoreDir)
        => IntegrationClosureBuilder.AddClosureProperties(propertyGroup, restoreDir);

    /// <summary>
    /// Convenience wrapper around <see cref="IntegrationClosureBuilder.AddClosureTargets"/>.
    /// </summary>
    public static void AddClosureTargets(XElement projectRoot)
        => IntegrationClosureBuilder.AddClosureTargets(projectRoot);
}
