// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Compression;

namespace Aspire.Cli.Tests.Utils;

/// <summary>
/// A folder-backed NuGet feed built from fabricated packages, plus a restore that can only reach it.
/// </summary>
/// <remarks>
/// Version-range behavior is decided by NuGet, not by the XML the CLI generates, so the only way to
/// prove a pin holds is to restore against a feed whose contents are known exactly.
/// <see cref="RestoreAsync"/> installs into a throwaway global packages folder so the fabricated
/// packages never enter the developer's or the agent's real cache. Reads are not isolated — the real
/// folder is supplied as a fallback so targeting packs still resolve, and NuGet treats a fallback
/// folder as a resolution source — so package ids used with this helper must still not exist on any
/// real feed or in the real cache, or a restore that should fail can succeed from it instead.
/// </remarks>
internal static class OfflineNuGetFeed
{
    /// <summary>
    /// Writes a minimal but valid <c>.nupkg</c> for <paramref name="id"/> at
    /// <paramref name="version"/> into <paramref name="feedPath"/>.
    /// </summary>
    public static void CreateStubPackage(string feedPath, string id, string version)
    {
        var stagingPath = Path.Combine(feedPath, $".staging-{id}");
        Directory.CreateDirectory(Path.Combine(stagingPath, "lib", "net10.0"));

        File.WriteAllText(Path.Combine(stagingPath, $"{id}.nuspec"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <description>Stub package for restore tests.</description>
                <authors>Aspire</authors>
              </metadata>
            </package>
            """);

        File.WriteAllText(Path.Combine(stagingPath, "[Content_Types].xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="nuspec" ContentType="text/xml" />
              <Default Extension="dll" ContentType="application/octet-stream" />
              <Default Extension="xml" ContentType="text/xml" />
            </Types>
            """);

        File.WriteAllBytes(Path.Combine(stagingPath, "lib", "net10.0", $"{id}.dll"), []);

        ZipFile.CreateFromDirectory(stagingPath, Path.Combine(feedPath, $"{id}.{version}.nupkg"));
        Directory.Delete(stagingPath, recursive: true);
    }

    /// <summary>
    /// Restores <paramref name="projectPath"/> against <paramref name="feedPath"/> only, into a
    /// throwaway global packages folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--source</c> replaces every configured source rather than adding one, which is what keeps
    /// the restore offline.
    /// </para>
    /// <para>
    /// <c>--packages</c> then keeps the fabricated packages out of the real global packages folder.
    /// Without it they are installed under their stated ids for good — NuGet never re-downloads a
    /// version already present — so a stub built here would silently satisfy a later restore
    /// anywhere on the machine. On its own <c>--packages</c> also hides the targeting packs the
    /// project needs (<c>NU1101 Microsoft.NETCore.App.Ref</c>), so the real folder is supplied as a
    /// fallback: lookups find it there, installs still go to the throwaway folder. A fallback folder
    /// that does not exist is a hard <c>NU1301</c>, hence the create.
    /// See https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders.
    /// </para>
    /// </remarks>
    public static async Task<(int ExitCode, string Output)> RestoreAsync(string projectPath, string feedPath)
    {
        using var packagesDirectory = new TempDirectory();

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!
        };

        startInfo.Environment["NUGET_FALLBACK_PACKAGES"] = EnsureGlobalPackagesFolder();

        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(feedPath);
        startInfo.ArgumentList.Add("--packages");
        startInfo.ArgumentList.Add(packagesDirectory.Path);

        using var process = Process.Start(startInfo)!;
        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask + await stderrTask);
    }

    /// <summary>
    /// The real global packages folder, used as a fallback so targeting packs still resolve.
    /// </summary>
    /// <remarks>
    /// <c>NUGET_PACKAGES</c> wins when it is set, which is how CI relocates the folder; otherwise
    /// NuGet's default is <c>~/.nuget/packages</c> on every platform. The directory is created when
    /// absent because NuGet fails a restore outright (<c>NU1301</c>) on a fallback folder that does
    /// not exist.
    /// </remarks>
    private static string EnsureGlobalPackagesFolder()
    {
        var folder = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// A directory that is deleted when the restore that used it is done.
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-offline-feed");

        public string Path => _directory.FullName;

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover throwaway folder is harmless; failing the test over it is not.
            }
        }
    }
}
