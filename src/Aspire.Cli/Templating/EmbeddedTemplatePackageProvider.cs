// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Templating;

/// <summary>
/// Materializes the Aspire.ProjectTemplates package that is embedded in the CLI assembly
/// (see <c>Aspire.Cli.csproj</c> EmbeddedResource entry) to a stable on-disk path that
/// can be handed to <c>dotnet new install</c>.
/// </summary>
/// <remarks>
/// <para>
/// Embedding the templates package inside the CLI binary eliminates all NuGet-based
/// resolution of templates. The package shipped inside any given CLI build is, by
/// construction, the templates package produced by the same build — so the version,
/// SHA, and channel of the templates can never drift from the CLI. Every acquisition
/// scenario (workspace build, localhive, PR build, daily, staging, stable) shares a
/// single deterministic path.
/// </para>
/// <para>
/// Extraction is lazy and idempotent. The nupkg is written to
/// <c>{AspireHomeDirectory}/templates/{cli-version}/Aspire.ProjectTemplates.nupkg</c>
/// the first time it is requested and reused on every subsequent invocation of the
/// same CLI build. Concurrent extractions from multiple processes are safe because
/// the final rename is atomic and equally-versioned binaries write byte-identical
/// payloads.
/// </para>
/// </remarks>
internal sealed class EmbeddedTemplatePackageProvider(CliExecutionContext executionContext)
{
    // Must match the LogicalName in Aspire.Cli.csproj (_ResolveEmbeddedTemplatesNupkg target).
    private const string EmbeddedResourceName = "Aspire.ProjectTemplates.nupkg";

    private const string PackageFileName = "Aspire.ProjectTemplates.nupkg";

    /// <summary>
    /// Gets <see langword="true"/> if the templates nupkg is embedded in the CLI assembly.
    /// Returns <see langword="false"/> on inner-loop builds that were produced with
    /// <c>SkipEmbeddedTemplatesNupkg=true</c> (rare; primarily a build-time escape hatch).
    /// </summary>
    public static bool HasEmbeddedPackage { get; } =
        typeof(EmbeddedTemplatePackageProvider).Assembly.GetManifestResourceInfo(EmbeddedResourceName) is not null;

    /// <summary>
    /// Returns the on-disk path to the embedded templates nupkg, extracting it on the
    /// first call. The path is stable for the lifetime of the running CLI binary, keyed
    /// by the CLI's informational version so a different CLI build never reuses another
    /// build's extracted payload.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the templates package is not embedded in the CLI assembly. This only
    /// happens for builds produced with <c>SkipEmbeddedTemplatesNupkg=true</c>.
    /// </exception>
    public async Task<FileInfo> EnsureExtractedAsync(CancellationToken cancellationToken)
    {
        var cliVersion = VersionHelper.GetDefaultTemplateVersion();

        // '+' is legal in path components on all supported OSes but tooling occasionally
        // treats it as a separator in display strings. Normalize to '_' for the cache
        // directory so the on-disk path is uniform regardless of build SHA suffix shape.
        var versionDirName = cliVersion.Replace('+', '_');

        var cacheDir = new DirectoryInfo(Path.Combine(
            executionContext.AspireHomeDirectory.FullName,
            "templates",
            versionDirName));
        var targetPath = Path.Combine(cacheDir.FullName, PackageFileName);

        if (File.Exists(targetPath))
        {
            return new FileInfo(targetPath);
        }

        cacheDir.Create();

        var assembly = typeof(EmbeddedTemplatePackageProvider).Assembly;
        await using var resourceStream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResourceName}' was not found in the CLI assembly. " +
                $"This build was produced with SkipEmbeddedTemplatesNupkg=true; rebuild without that property.");

        // Write to a sibling temp file and rename so concurrent CLI processes never see
        // a partially-written nupkg. Equally-versioned binaries write byte-identical
        // payloads so the rename winner does not matter.
        var tempPath = targetPath + ".tmp-" + Path.GetRandomFileName();
        try
        {
            await using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await resourceStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                File.Move(tempPath, targetPath);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                // Another process beat us. The byte-for-byte identical payload is already
                // there; drop our copy and use the existing file.
                TryDelete(tempPath);
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return new FileInfo(targetPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; a leftover .tmp-* file is harmless.
        }
    }
}
