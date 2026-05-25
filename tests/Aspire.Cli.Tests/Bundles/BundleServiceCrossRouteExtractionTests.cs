// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;

namespace Aspire.Cli.Tests.Bundles;

/// <summary>
/// Verifies <see cref="BundleService.ComputeDefaultExtractDir(string)"/> against
/// every (source × prefix shape) combination the supported install routes produce.
/// The matrix locks in the contract: the reader walks the sidecar's
/// <c>source</c> field — and nothing else — to decide between
/// <c>binaryDir</c> (winget / brew / dotnet-tool) and the parent of
/// <c>binaryDir</c> (script / pr / localhive). Missing, invalid, or unknown sidecars fall
/// through to the parent-of-binary heuristic.
/// </summary>
public class BundleServiceCrossRouteExtractionTests
{
    // The inline-data paths use forward slashes for source-readability; the test
    // method converts them to platform-native separators before constructing
    // absolute paths under the temp root.
    [Theory]
    // 1) winget canonical: WinGet places the portable binary in a per-package
    //    versioned directory; the bundle payload lives in that same directory.
    [InlineData("winget", "WinGet/Packages/Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe/aspire.exe", "WinGet/Packages/Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe")]
    // 2) brew canonical: Homebrew cellar with versioned cask directory.
    [InlineData("brew", "Caskroom/aspire/13.2.0/aspire", "Caskroom/aspire/13.2.0")]
    // 3) dotnet-tool canonical: dotnet's global-tools shim at ~/.dotnet/tools/aspire
    //    delegates (apphost launch on Windows; symlink follow on Unix) to the
    //    RID-specific native binary inside the .store directory tree. The
    //    binary the running process actually points at — and where the
    //    payload-embedded sidecar lives — is that RID dir.
    [InlineData("dotnet-tool", ".dotnet/tools/.store/aspire.cli/13.2.0/aspire.cli.linux-x64/13.2.0/tools/net10.0/linux-x64/aspire", ".dotnet/tools/.store/aspire.cli/13.2.0/aspire.cli.linux-x64/13.2.0/tools/net10.0/linux-x64")]
    // 4) script canonical: ~/.aspire/bin/aspire with bundle in ~/.aspire/.
    [InlineData("script", ".aspire/bin/aspire", ".aspire")]
    // 5) PR-script canonical: per-PR dogfood prefix.
    [InlineData("pr", ".aspire/dogfood/pr-16817/bin/aspire", ".aspire/dogfood/pr-16817")]
    // 6) localhive canonical: local dev hive with the same bin layout as script.
    [InlineData("localhive", ".aspire/local/bin/aspire", ".aspire/local")]
    // 7) Cross-route smuggle case: a brew-source sidecar dropped into a
    //    script-layout prefix MUST resolve to binaryDir per the switch — the
    //    reader is honest about whatever the producer put on disk. When the
    //    producer side correctly suppresses the smuggled sidecar, this row's
    //    input condition never arises in practice; the test verifies the
    //    reader's behavior is well-defined and consistent for all inputs.
    [InlineData("brew", ".aspire/dogfood/pr-16817/bin/aspire", ".aspire/dogfood/pr-16817/bin")]
    // 8) script source dropped at a flat-cellar layout (misuse, but defined):
    //    script maps to parent-of-binary, so the result is one level above the
    //    cask version dir. Not a real install pattern; locks in determinism.
    [InlineData("script", "Caskroom/aspire/13.2.0/aspire", "Caskroom/aspire")]
    // 9) No sidecar at all: fallback heuristic = parent of binaryDir.
    [InlineData(null, ".aspire/bin/aspire", ".aspire")]
    // 10) Sidecar with invalid JSON: parser throws, treated as no sidecar.
    [InlineData("__invalid__", ".aspire/bin/aspire", ".aspire")]
    // 11) Sidecar with an unknown source value: switch default arm, same as
    //     missing sidecar — parent-of-binary.
    [InlineData("github-actions", ".aspire/bin/aspire", ".aspire")]
    public void ComputeDefaultExtractDir_RouteAndPrefixCombinations_ProduceExpectedExtractDir(
        string? sourceField,
        string relativeProcessPath,
        string relativeExpectedExtractDir)
    {
        var root = Directory.CreateTempSubdirectory("aspire-extract-dir-").FullName;
        try
        {
            var processPath = Path.Combine(root, ToNativePath(relativeProcessPath));
            var binaryDir = Path.GetDirectoryName(processPath)!;
            Directory.CreateDirectory(binaryDir);
            File.WriteAllText(processPath, "fake-binary");

            if (sourceField is not null)
            {
                var sidecarPath = Path.Combine(binaryDir, ".aspire-install.json");
                var content = sourceField switch
                {
                    "__invalid__" => "{not valid json",
                    _ => $"{{\"source\":\"{sourceField}\"}}",
                };
                File.WriteAllText(sidecarPath, content);
            }

            var expected = Path.Combine(root, ToNativePath(relativeExpectedExtractDir));

            var actual = BundleService.ComputeDefaultExtractDir(processPath);

            Assert.Equal(expected, actual);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private static string ToNativePath(string forwardSlashPath)
        => forwardSlashPath.Replace('/', Path.DirectorySeparatorChar);
}
