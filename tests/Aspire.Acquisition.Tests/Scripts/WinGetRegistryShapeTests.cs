// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Microsoft.Win32;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Verifies the actual ARP registry shape that real winget writes when installing a
/// zip+portable package, so that drift in winget's wire format (key names, value types,
/// which fields are present) is caught before it silently breaks the install-detection
/// matcher in <c>Aspire.Cli.Acquisition.WingetAspireEntryMatcher</c>.
///
/// Why this exists: the unit tests for the matcher are agnostic to wire names (they call
/// <c>Matches(...)</c> with strings directly), and the HKCU integration tests in
/// Aspire.Cli.Tests write the values they expect to read — both are self-referential.
/// This test goes through real <c>winget install</c> against a synthetic
/// zip+portable manifest and asserts the actual values winget writes, so changes in
/// the winget wire format are caught here rather than in a customer's self-extract bug
/// report.
///
/// Tradeoff: a real <c>winget install</c>/uninstall cycle takes ~10–30s and mutates user
/// state (registry, PATH, %LOCALAPPDATA%\Microsoft\WinGet\Packages). Cleanup runs in
/// <c>finally</c> via <c>winget uninstall --id &lt;unique-id&gt; --silent</c>, and the
/// package identifier is suffixed with a per-run GUID so a crashed run still leaves
/// identifiable, grep-able state.
///
/// Loud-fail rather than silent-skip: on Windows we deliberately do not use
/// <c>[RequiresTools]</c>. If winget is missing on a Windows test host the test throws
/// with an actionable message so we can see in CI logs that we're not silently
/// skipping the only test that exercises real winget against the matcher.
/// </summary>
[Collection(nameof(WinGetRegistryShapeTestCollection))]
[SupportedOSPlatform("windows")]
public sealed class WinGetRegistryShapeTests(ITestOutputHelper testOutput)
{
    private readonly ITestOutputHelper _testOutput = testOutput;

    [Fact]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "winget is Windows-only.")]
    public async Task WinGet_Install_Of_PortableZip_Writes_ARP_Values_Matcher_Reads()
    {
        // Loud-fail (not skip) if winget isn't on PATH on a Windows host. GitHub-hosted
        // windows-latest (windows-2022) images ship App Installer / winget pre-installed
        // since mid-2023, so absence is a CI-image regression we want to surface, not hide.
        var wingetPath = LocateWinGetOrThrow();
        _testOutput.WriteLine($"winget: {wingetPath}");

        // Local manifest install is an opt-in winget feature. dogfood.ps1 and
        // prepare-manifest-artifact.ps1 both flip this on the same way. Toggling the
        // setting requires admin on a first-time machine. GH Actions windows-latest
        // runners are admin by default; developer machines often are not. If we are
        // not admin AND the setting isn't already enabled, the install would later
        // fail with the cryptic 0x8a150004 "Opening manifest failed" — skip with an
        // actionable message instead so the failure is recognisable and avoidable.
        var enable = await RunAsync(wingetPath, ["settings", "--enable", "LocalManifestFiles"], allowFailure: true);
        if (enable.ExitCode != 0)
        {
            _testOutput.WriteLine($"winget settings --enable LocalManifestFiles failed (exit {enable.ExitCode}).");
            _testOutput.WriteLine($"stdout: {enable.Stdout}");
            _testOutput.WriteLine($"stderr: {enable.Stderr}");

            if (!IsCurrentProcessElevated())
            {
                Assert.Skip(
                    "This test requires winget LocalManifestFiles to be enabled. Toggling that setting needs an " +
                    "elevated shell on first use. Either rerun the test from an elevated PowerShell, or enable the " +
                    "setting once (admin) with: winget settings --enable LocalManifestFiles. CI hosts run elevated " +
                    "so this skip never fires there.");
            }

            // Elevated but still failed — that's a real environment issue (winget too
            // old, App Installer not present, etc.). Fall through to the install
            // attempt below, which surfaces the underlying 0x8a150004 as a loud
            // failure with full context.
        }

        var packageSuffix = Guid.NewGuid().ToString("N")[..8];
        var packageId = $"AspireTest.WinGetShape.{packageSuffix}";
        var packageVersion = "0.0.1";
        var commandAlias = $"aspiretest-winget-shape-{packageSuffix}".ToLowerInvariant();
        var archiveName = $"aspiretest-winget-shape-{packageSuffix}.zip";

        using var env = new TestEnvironment();
        var stageDir = Path.Combine(env.TempDirectory, "stage");
        var manifestDir = Path.Combine(stageDir, "manifest");
        var archiveDir = Path.Combine(stageDir, "archive");
        Directory.CreateDirectory(manifestDir);
        Directory.CreateDirectory(archiveDir);

        var archivePath = Path.Combine(archiveDir, archiveName);
        await WriteSyntheticZipAsync(archivePath, payloadName: "aspire.exe", payloadContent: $"stub-{packageSuffix}");
        var archiveSha256 = await ComputeSha256HexAsync(archivePath);
        _testOutput.WriteLine($"archive: {archivePath} sha256={archiveSha256}");

        // Loopback HttpListener — winget downloads InstallerUrl via WinINet's
        // InternetOpenUrl(), which only supports http/https/ftp (not file://). See
        // Start-LocalArchiveServer in eng/winget/dogfood.ps1 for the same workaround.
        // Loopback prefixes don't require admin / netsh urlacl reservation.
        using var server = LoopbackHttpFileServer.Start(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [archiveName] = archivePath,
            },
            contentType: "application/octet-stream");
        _testOutput.WriteLine($"loopback: {server.BaseUrl}");

        // PackageLocale is required by the singleton schema's top-level "required" array; the
        // multi-file shape (used by the real Aspire manifest) carries it in a separate
        // *.locale.<tag>.yaml so it's easy to forget on a hand-rolled singleton. Without it,
        // winget rejects the manifest with 0x8A150004 "Opening manifest failed" before any
        // install/download attempt — see
        // https://github.com/microsoft/winget-cli/blob/master/schemas/JSON/manifests/v1.6.0/manifest.singleton.1.6.0.json
        var manifestYaml = $$"""
            # yaml-language-server: $schema=https://aka.ms/winget-manifest.singleton.1.6.0.schema.json
            PackageIdentifier: {{packageId}}
            PackageVersion: {{packageVersion}}
            PackageLocale: en-US
            PackageName: Aspire WinGet Shape Test {{packageSuffix}}
            Publisher: AspireTest
            License: MIT
            ShortDescription: Synthetic package used to snapshot the ARP registry shape that winget writes for zip+portable installs.
            InstallerType: zip
            NestedInstallerType: portable
            NestedInstallerFiles:
            - RelativeFilePath: aspire.exe
              PortableCommandAlias: {{commandAlias}}
            Commands:
            - {{commandAlias}}
            InstallModes:
            - silent
            UpgradeBehavior: uninstallPrevious
            Installers:
            - Architecture: {{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64")}}
              InstallerUrl: {{server.BaseUrl}}/{{archiveName}}
              InstallerSha256: {{archiveSha256.ToUpperInvariant()}}
            ManifestType: singleton
            ManifestVersion: 1.6.0
            """;
        var manifestPath = Path.Combine(manifestDir, $"{packageId}.yaml");
        await File.WriteAllTextAsync(manifestPath, manifestYaml);
        _testOutput.WriteLine($"manifest: {manifestPath}");

        try
        {
            // --disable-interactivity: belt-and-suspenders alongside --accept-*-agreements.
            // If a future winget adds a new prompt type, this still fails fast instead of
            // hanging this test process (which has no stdin).
            var install = await RunAsync(wingetPath,
                ["install", "--manifest", manifestPath, "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                allowFailure: true);
            if (install.ExitCode != 0)
            {
                // 0x8a150004 is APPINSTALLER_CLI_ERROR_MANIFEST_FAILED — typically because
                // LocalManifestFiles is not enabled in winget settings (which requires admin
                // to enable on first use).
                var hint = unchecked((int)0x8a150004) == install.ExitCode
                    ? " Likely cause: LocalManifestFiles is not enabled in winget settings. " +
                      "Run elevated: 'winget settings --enable LocalManifestFiles'. " +
                      "On GH Actions windows-latest this should already be admin-runnable."
                    : string.Empty;
                Assert.Fail($"winget install failed (exit 0x{install.ExitCode:x8}).{hint}\nstdout:\n{install.Stdout}\nstderr:\n{install.Stderr}");
            }

            // The ARP subkey for a locally-installed singleton package is named
            // "<PackageIdentifier>_<scope-suffix>" under
            // HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall. The exact suffix
            // depends on winget's source bookkeeping for local installs, so enumerate.
            var (subkeyName, snapshot) = ReadAspireTestArpEntry(packageId);
            _testOutput.WriteLine($"ARP subkey: {subkeyName}");
            foreach (var kvp in snapshot)
            {
                _testOutput.WriteLine($"  {kvp.Key} = ({kvp.Value?.GetType().Name ?? "null"}) {kvp.Value}");
            }

            // The wire-name + presence + type contract that
            // WingetAspireEntryMatcher.Matches() depends on. If any of these assertions
            // start failing, winget changed its ARP wire format and the matcher needs
            // to be updated in lockstep.
            Assert.Equal(packageId, Assert.IsType<string>(snapshot["WinGetPackageIdentifier"]));
            Assert.Equal("portable", Assert.IsType<string>(snapshot["WinGetInstallerType"]));
            var installLocation = Assert.IsType<string>(snapshot["InstallLocation"]);
            Assert.False(string.IsNullOrWhiteSpace(installLocation), "InstallLocation must be a non-empty string.");
            Assert.True(Directory.Exists(installLocation), $"InstallLocation '{installLocation}' must point at an extant directory.");

            // The aspire.exe extracted from the zip lives directly under InstallLocation
            // (NestedInstallerFiles[].RelativeFilePath relative to extraction root) —
            // this is exactly the "binary colocated with InstallLocation" relationship
            // that WingetAspireEntryMatcher.IsProcessUnderDirectory relies on.
            var extractedAspireExe = Path.Combine(installLocation, "aspire.exe");
            Assert.True(File.Exists(extractedAspireExe), $"Expected extracted payload at '{extractedAspireExe}'.");
        }
        finally
        {
            try
            {
                // --accept-source-agreements + --disable-interactivity: the uninstall path
                // touches winget's configured sources (e.g. msstore) which can otherwise
                // prompt for a one-time source agreement. There is no stdin in this test
                // process, so any prompt fails with 0x8A150042 "Error reading input in
                // prompt" and leaves the package half-uninstalled. Pass both so cleanup
                // is fully non-interactive.
                var uninstall = await RunAsync(wingetPath, ["uninstall", "--id", packageId, "--silent", "--accept-source-agreements", "--disable-interactivity"], allowFailure: true);
                _testOutput.WriteLine($"winget uninstall exit={uninstall.ExitCode}");
                if (uninstall.ExitCode != 0)
                {
                    _testOutput.WriteLine($"uninstall stdout:\n{uninstall.Stdout}\nstderr:\n{uninstall.Stderr}");
                }
            }
            catch (Exception ex)
            {
                _testOutput.WriteLine($"winget uninstall threw: {ex}");
            }
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string LocateWinGetOrThrow()
    {
        var winget = Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, "winget.exe"))
            .FirstOrDefault(File.Exists);

        if (winget is null)
        {
            throw new InvalidOperationException(
                "winget.exe was not found on PATH. GitHub-hosted windows-latest runner images " +
                "ship App Installer / winget pre-installed; if running locally, install 'App Installer' " +
                "from the Microsoft Store. We intentionally do not [RequiresTools(\"winget\")] here so " +
                "that an image regression surfaces as a loud failure rather than a silent skip.");
        }

        return winget;
    }

    private static async Task WriteSyntheticZipAsync(string path, string payloadName, string payloadContent)
    {
        await using var fs = File.Create(path);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(payloadName, CompressionLevel.NoCompression);
        await using var s = entry.Open();
        await s.WriteAsync(Encoding.UTF8.GetBytes(payloadContent));
    }

    private static async Task<string> ComputeSha256HexAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs);
        return Convert.ToHexString(hash);
    }

    [SupportedOSPlatform("windows")]
    private static (string SubkeyName, Dictionary<string, object?> Values) ReadAspireTestArpEntry(string packageId)
    {
        const string ArpPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        using var arp = Registry.CurrentUser.OpenSubKey(ArpPath)
            ?? throw new InvalidOperationException($"HKCU\\{ArpPath} does not exist.");

        // Locally-installed packages get a subkey name "<PackageIdentifier>_<scope-suffix>"
        // — match by prefix so we don't have to hardcode the suffix shape.
        var match = arp.GetSubKeyNames().FirstOrDefault(name => name.StartsWith(packageId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var sample = string.Join(", ", arp.GetSubKeyNames().Where(n => n.Contains("AspireTest", StringComparison.OrdinalIgnoreCase)));
            throw new InvalidOperationException(
                $"No ARP subkey starting with '{packageId}' under HKCU\\{ArpPath}. " +
                $"AspireTest-related subkeys present: [{sample}].");
        }

        using var entry = arp.OpenSubKey(match)
            ?? throw new InvalidOperationException($"Failed to open HKCU\\{ArpPath}\\{match} for reading.");

        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in entry.GetValueNames())
        {
            values[name] = entry.GetValue(name);
        }

        return (match, values);
    }

    private async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        _testOutput.WriteLine($"> {fileName} {string.Join(' ', args)}");

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!allowFailure && proc.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{fileName} {string.Join(' ', args)}' failed with exit code {proc.ExitCode}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }

        return new ProcessResult(proc.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

/// <summary>
/// Forces all real-winget tests to run serially. Real <c>winget install</c>/uninstall
/// mutates a single process-wide settings store and the per-user PATH; parallel runs
/// can race or trip winget's internal mutex.
/// </summary>
[CollectionDefinition(nameof(WinGetRegistryShapeTestCollection), DisableParallelization = true)]
public sealed class WinGetRegistryShapeTestCollection;
#endif
