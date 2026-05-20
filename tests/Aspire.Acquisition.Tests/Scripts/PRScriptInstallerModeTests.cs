// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests for package-manager installer modes on get-aspire-cli-pr.{sh,ps1}.
/// </summary>
public class PRScriptInstallerModeTests(ITestOutputHelper testOutput)
{
    private readonly ITestOutputHelper _testOutput = testOutput;

    private async Task<ScriptToolCommand> CreateBashCommandWithMockGhAsync(TestEnvironment env)
    {
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        return cmd;
    }

    private async Task<ScriptToolCommand> CreatePsCommandWithMockGhAsync(TestEnvironment env)
    {
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        return cmd;
    }

    private static async Task<string> CreateHomebrewInstallerArtifactAsync(string root)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "aspire.rb"), "cask \"aspire\" do\n  version \"13.3.0\"\nend\n");
        await File.WriteAllTextAsync(Path.Combine(root, "dogfood.sh"), "#!/usr/bin/env bash\nexit 0\n");
        await FakeArchiveHelper.CreateFakeNupkgAsync(root, "Aspire.Cli", "13.3.0-pr.1234.abc");
        await FakeArchiveHelper.CreateFakeNupkgAsync(root, "Aspire.Hosting", "13.3.0-pr.1234.abc");
        return root;
    }

    private static async Task<string> CreateWinGetInstallerArtifactAsync(string root)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Microsoft.Aspire.installer.yaml"), "PackageIdentifier: Microsoft.Aspire\nPackageVersion: 13.3.0\nInstallers: []\n");
        await File.WriteAllTextAsync(Path.Combine(root, "dogfood.ps1"), "exit 0\n");
        await FakeArchiveHelper.CreateFakeNupkgAsync(root, "Aspire.Cli", "13.3.0-pr.1234.abc");
        await FakeArchiveHelper.CreateFakeNupkgAsync(root, "Aspire.Hosting", "13.3.0-pr.1234.abc");
        return root;
    }

    // Builds the realistic PR-channel layout that prepare-manifest-artifact.ps1 produces:
    // an installer.yaml with two Installers entries (https:// URLs and a placeholder
    // SHA256 of all zeros) co-located with dogfood.ps1, plus fake aspire-cli-win-*
    // archives. The archives are nested under <archive-root>/Debug/Shipping/ to mirror
    // what `gh run download cli-native-archives-<rid>` produces — that artifact is
    // uploaded with `path: artifacts/packages/**/aspire-cli*` in
    // .github/workflows/build-cli-native-archives.yml, so the `**/` glob preserves the
    // Debug/Shipping/ directory structure. dogfood.ps1's HTTP server is required to
    // serve files regardless of how deep they are under -ArchiveRoot; a flat fixture
    // hid that requirement and let a real-world 404 ship.
    private static async Task<(string ManifestDir, string ArchiveRoot)> CreateWinGetPrChannelArtifactAsync(string root, string version = "13.3.0")
    {
        var manifestDir = Path.Combine(root, "installer-winget");
        var archiveRoot = Path.Combine(root, "installer-native-archives");
        // Archives live under Debug/Shipping/ when `gh run download` extracts the
        // cli-native-archives-<rid> artifact — see comment above.
        var archiveDir = Path.Combine(archiveRoot, "Debug", "Shipping");
        Directory.CreateDirectory(manifestDir);
        Directory.CreateDirectory(archiveDir);

        var placeholder = new string('0', 64);
        var installerYaml = $$"""
            # yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.10.0.schema.json
            PackageIdentifier: Microsoft.Aspire
            PackageVersion: "{{version}}"
            InstallerType: zip
            NestedInstallerType: portable
            NestedInstallerFiles:
            - RelativeFilePath: aspire.exe
              PortableCommandAlias: aspire
            Installers:
            - Architecture: x64
              InstallerUrl: https://ci.dot.net/public/aspire/{{version}}/aspire-cli-win-x64-{{version}}.zip
              InstallerSha256: {{placeholder}}
            - Architecture: arm64
              InstallerUrl: https://ci.dot.net/public/aspire/{{version}}/aspire-cli-win-arm64-{{version}}.zip
              InstallerSha256: {{placeholder}}
            ManifestType: installer
            ManifestVersion: 1.10.0
            """;
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "Microsoft.Aspire.installer.yaml"), installerYaml);
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "Microsoft.Aspire.yaml"), $"PackageIdentifier: Microsoft.Aspire\nPackageVersion: {version}\nManifestType: version\nManifestVersion: 1.10.0\n");
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "Microsoft.Aspire.locale.en-US.yaml"), $"PackageIdentifier: Microsoft.Aspire\nPackageVersion: {version}\nPackageLocale: en-US\nManifestType: defaultLocale\nManifestVersion: 1.10.0\n");
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "dogfood.ps1"), "exit 0\n");

        // Distinct fake bytes per RID so SHA256 differences flow into the mock install check.
        // Real zips with a stub aspire.exe at the root, mirroring the contract real winget
        // expects (NestedInstallerFiles[].RelativeFilePath must exist after extraction).
        // The mock winget extracts these and checks the contract — see CreateMockWinGetBinAsync.
        await WriteRealAspireWinGetZipAsync(Path.Combine(archiveDir, $"aspire-cli-win-x64-{version}.zip"), $"stub-x64-{version}");
        await WriteRealAspireWinGetZipAsync(Path.Combine(archiveDir, $"aspire-cli-win-arm64-{version}.zip"), $"stub-arm64-{version}");

        return (manifestDir, archiveRoot);
    }

    private static async Task<string> CreateMockHomebrewBinAsync(TestEnvironment env, int aspireExitCode)
    {
        var mockBinDir = Path.Combine(env.TempDirectory, "mock-homebrew-bin");
        var brewRepository = Path.Combine(env.TempDirectory, "brew-repository");
        var brewPrefix = Path.Combine(env.TempDirectory, "brew-prefix");
        var brewLog = Path.Combine(env.TempDirectory, "brew.log");

        Directory.CreateDirectory(mockBinDir);
        Directory.CreateDirectory(brewRepository);
        Directory.CreateDirectory(brewPrefix);

        var brewPath = Path.Combine(mockBinDir, "brew");
        await File.WriteAllTextAsync(brewPath, $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            echo "$*" >> "{{brewLog}}"

            create_aspire() {
              cat > "{{mockBinDir}}/aspire" <<'ASPIRE'
            #!/usr/bin/env bash
            echo "mock aspire failure"
            exit {{aspireExitCode}}
            ASPIRE
              chmod +x "{{mockBinDir}}/aspire"
            }

            case "${1:-}" in
              --repository)
                echo "{{brewRepository}}"
                exit 0
                ;;
              --prefix)
                echo "{{brewPrefix}}"
                exit 0
                ;;
              list)
                exit 0
                ;;
              tap-info)
                exit 1
                ;;
              tap-new)
                tap="${@: -1}"
                org="${tap%%/*}"
                repo="${tap##*/}"
                mkdir -p "{{brewRepository}}/Library/Taps/$org/homebrew-$repo"
                exit 0
                ;;
              style|audit|info)
                exit 0
                ;;
              install)
                create_aspire
                exit 0
                ;;
              uninstall)
                rm -f "{{mockBinDir}}/aspire"
                exit 0
                ;;
              untap)
                exit 0
                ;;
            esac

            echo "unexpected brew command: $*" >&2
            exit 1
            """);
        FileHelper.MakeExecutable(brewPath);

        var curlPath = Path.Combine(mockBinDir, "curl");
        await File.WriteAllTextAsync(curlPath, """
            #!/usr/bin/env bash
            printf '404'
            exit 0
            """);
        FileHelper.MakeExecutable(curlPath);

        return mockBinDir;
    }

    // Mock winget for Windows. Mirrors enough of real winget's behaviour that
    // dogfood.ps1 regressions surface here:
    //   * `validate --manifest <dir>` and `install --manifest <dir>` scan every file
    //     in <dir> and reject non-yaml entries.
    //   * `validate` enforces the schema rule that InstallerUrl matches ^https?://.
    //   * `install` actually downloads each InstallerUrl over the wire (via
    //     Invoke-WebRequest) and verifies the SHA256 of the bytes it received against
    //     InstallerSha256. This catches both schemes real winget can't fetch (real
    //     winget uses WinINet's InternetOpenUrl which doesn't support file://) and
    //     stale InstallerSha256 entries — both bugs we have already shipped in PRs.
    //   * `install` honors the manifest-vs-archive contract: for InstallerType: zip
    //     it extracts the downloaded zip and requires every NestedInstallerFiles[]
    //     .RelativeFilePath to exist after extraction. Real winget fails at this step
    //     with an opaque error (observed 0x8A150001 with no diag-log line beyond
    //     "Started applying motw"); catching it here makes the failure attributable.
    private static async Task<string> CreateMockWinGetBinAsync(TestEnvironment env, int aspireExitCode)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Mock winget is Windows-only (winget itself is Windows-only).");
        }

        var mockBinDir = Path.Combine(env.TempDirectory, "mock-winget-bin");
        var wingetLog = Path.Combine(env.TempDirectory, "winget.log");

        Directory.CreateDirectory(mockBinDir);

        // PowerShell implementation of the mock. Kept in a separate file so it can be
        // edited as PowerShell rather than escaped through a .cmd shim.
        var implPath = Path.Combine(mockBinDir, "winget-impl.ps1");
        await File.WriteAllTextAsync(implPath, $$"""
            param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)
            $ErrorActionPreference = 'Stop'
            $cmd = if ($Args.Count -ge 1) { $Args[0] } else { '' }
            $rest = if ($Args.Count -ge 2) { $Args[1..($Args.Count - 1)] } else { @() }
            Add-Content -LiteralPath '{{wingetLog.Replace("\\", "\\\\")}}' -Value ($Args -join ' ')

            switch ($cmd) {
                'list'              { exit 1 }
                'settings'          { exit 0 }
                'uninstall'         { exit 0 }
                { $_ -in 'validate','install' } { }
                default {
                    Write-Error "Mock winget: unexpected command: $cmd"
                    exit 1
                }
            }

            $manifestDir = $null
            for ($i = 0; $i -lt $rest.Count - 1; $i++) {
                if ($rest[$i] -eq '--manifest') { $manifestDir = $rest[$i + 1]; break }
            }
            if (-not $manifestDir -or -not (Test-Path -LiteralPath $manifestDir)) {
                Write-Error "Mock winget: --manifest <dir> required"
                exit 1
            }

            # Real winget treats every file in the manifest dir as a manifest and rejects
            # non-yaml entries (e.g. "The manifest does not contain a valid root.
            # File: dogfood.ps1"). Mirror that so manifest-staging regressions in
            # dogfood.ps1 are caught here.
            foreach ($f in Get-ChildItem -LiteralPath $manifestDir -File) {
                if ($f.Extension -notin '.yaml', '.yml') {
                    Write-Error "Mock winget: non-yaml file in manifest dir: $($f.Name)"
                    exit 1
                }
            }

            $installerYaml = Get-ChildItem -LiteralPath $manifestDir -File -Filter '*.installer.yaml' | Select-Object -First 1
            if (-not $installerYaml) { exit 0 }

            $entries = @()
            $current = $null
            $nestedFiles = @()
            foreach ($line in Get-Content -LiteralPath $installerYaml.FullName) {
                if ($line -match '^\s*InstallerUrl:\s*(\S+)\s*$') {
                    if ($current) { $entries += [pscustomobject]$current }
                    $current = @{ Url = $Matches[1]; Sha = $null }
                } elseif ($line -match '^\s*InstallerSha256:\s*(\S+)\s*$' -and $current) {
                    $current.Sha = $Matches[1].ToUpperInvariant()
                } elseif ($line -match '^\s*-?\s*RelativeFilePath:\s*(\S+)\s*$') {
                    # NestedInstallerFiles is shared across all Installers in the manifest,
                    # not per-entry. Collect them once and apply to every downloaded zip.
                    $nestedFiles += $Matches[1]
                }
            }
            if ($current) { $entries += [pscustomobject]$current }

            foreach ($e in $entries) {
                # Schema rule: InstallerUrl must match ^https?://. Real winget enforces
                # this in `validate`; in `install`, WinINet's InternetOpenUrl() rejects
                # any other scheme (file://, ftp://, etc) with HRESULT 0x8007007b. Mock
                # both layers so PR-script regressions can't sneak past tests.
                if ($e.Url -notmatch '^(?i)https?://') {
                    Write-Error "Mock winget ${cmd}: InstallerUrl '$($e.Url)' does not match ^https?://"
                    exit 1
                }
            }

            if ($cmd -eq 'install') {
                foreach ($e in $entries) {
                    $tmp = New-TemporaryFile
                    $renamedZip = $null
                    $extractDir = $null
                    try {
                        try {
                            Invoke-WebRequest -Uri $e.Url -OutFile $tmp.FullName -UseBasicParsing -TimeoutSec 30 | Out-Null
                        } catch {
                            Write-Error "Mock winget install: failed to download $($e.Url): $_"
                            exit 1
                        }
                        $actual = (Get-FileHash -LiteralPath $tmp.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
                        if ($e.Sha -and $actual -ne $e.Sha) {
                            Write-Error "Mock winget install: InstallerSha256 mismatch for $($e.Url) (expected $($e.Sha), got $actual)"
                            exit 1
                        }

                        # Manifest-vs-archive contract: for InstallerType: zip with
                        # NestedInstallerType: portable, real winget extracts the zip
                        # and requires every NestedInstallerFiles[].RelativeFilePath to
                        # exist after extraction. If it doesn't, real winget fails at
                        # install time with an opaque error (we observed 0x8A150001
                        # with no diag-log line beyond "Started applying motw"). Catch
                        # the contract violation here so the failure is attributable.
                        if ($nestedFiles.Count -gt 0) {
                            $renamedZip = "$($tmp.FullName).zip"
                            Move-Item -LiteralPath $tmp.FullName -Destination $renamedZip
                            $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
                            New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
                            try {
                                Expand-Archive -LiteralPath $renamedZip -DestinationPath $extractDir -Force
                            } catch {
                                Write-Error "Mock winget install: failed to extract $($e.Url): $_"
                                exit 1
                            }
                            foreach ($rel in $nestedFiles) {
                                # RelativeFilePath uses '/' as separator in YAML even on Windows.
                                $relNative = $rel -replace '/', [System.IO.Path]::DirectorySeparatorChar
                                $expected = Join-Path $extractDir $relNative
                                if (-not (Test-Path -LiteralPath $expected)) {
                                    Write-Error "Mock winget install: NestedInstallerFiles entry '$rel' is missing from extracted archive $($e.Url)"
                                    exit 1
                                }
                            }
                        }
                    } finally {
                        if ($extractDir -and (Test-Path -LiteralPath $extractDir)) {
                            Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
                        }
                        if ($renamedZip -and (Test-Path -LiteralPath $renamedZip)) {
                            Remove-Item -LiteralPath $renamedZip -Force -ErrorAction SilentlyContinue
                        }
                        if (Test-Path -LiteralPath $tmp.FullName) {
                            Remove-Item -LiteralPath $tmp.FullName -ErrorAction SilentlyContinue
                        }
                    }
                }
            }

            exit 0
            """);

        // .cmd shim so the mock is invokable as 'winget' from PATH (PowerShell honors
        // PATHEXT and resolves 'winget' to winget.cmd before scanning system paths).
        var wingetCmd = Path.Combine(mockBinDir, "winget.cmd");
        await File.WriteAllTextAsync(wingetCmd, """
            @echo off
            pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0winget-impl.ps1" %*
            """);

        var aspireCmd = Path.Combine(mockBinDir, "aspire.cmd");
        await File.WriteAllTextAsync(aspireCmd, $$"""
            @echo off
            echo mock aspire {{(aspireExitCode == 0 ? "version" : "failure")}}
            exit /b {{aspireExitCode}}
            """);

        return mockBinDir;
    }

    private static async Task CreateFakeHomebrewArchivesAsync(string root)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "aspire-cli-osx-arm64-13.3.0.tar.gz"), "fake arm64 archive");
        await File.WriteAllTextAsync(Path.Combine(root, "aspire-cli-osx-x64-13.3.0.tar.gz"), "fake x64 archive");
    }

    private static async Task CreateFakeWinGetArchivesAsync(string root)
    {
        var archiveDir = Path.Combine(root, "Debug", "Shipping");
        Directory.CreateDirectory(archiveDir);
        // Real zips with a stub aspire.exe at the root so prepare-manifest-artifact.ps1
        // sees a valid archive layout and downstream contract tests (extraction +
        // NestedInstallerFiles lookup) work against the same fixture.
        await WriteRealAspireWinGetZipAsync(Path.Combine(archiveDir, "aspire-cli-win-x64-13.3.0.zip"), "stub-x64-13.3.0");
        await WriteRealAspireWinGetZipAsync(Path.Combine(archiveDir, "aspire-cli-win-arm64-13.3.0.zip"), "stub-arm64-13.3.0");
    }

    private static async Task<string> GetSha256HexAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    // Writes a real .zip containing a stub `aspire.exe` at the root, matching the
    // top-level layout of the real aspire-cli-win-*.zip artifacts. The bytes are
    // tiny stubs — winget and our tests only care about the extraction layout
    // (NestedInstallerFiles[].RelativeFilePath = aspire.exe), not signed binary
    // content. The `aspireExeContent` argument is hashed into the zip so callers
    // can produce zips with distinct SHA256 hashes per RID.
    private static async Task WriteRealAspireWinGetZipAsync(string path, string aspireExeContent)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        await using var fs = File.Create(path);
        using var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry("aspire.exe", System.IO.Compression.CompressionLevel.NoCompression);
        await using var s = entry.Open();
        await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes(aspireExeContent));
    }

    // Parses NestedInstallerFiles[].RelativeFilePath entries from an installer manifest
    // YAML. Uses a regex instead of a YAML parser to avoid pulling in YamlDotNet just
    // for the test project (we already do regex parsing of InstallerUrl/InstallerSha256
    // elsewhere in this file and in the mock winget script).
    private static IReadOnlyList<string> ParseRelativeFilePaths(string installerYaml)
    {
        var paths = new List<string>();
        foreach (var rawLine in installerYaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*-?\s*RelativeFilePath:\s*(\S+)\s*$");
            if (m.Success)
            {
                paths.Add(m.Groups[1].Value);
            }
        }
        return paths;
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_Help_DescribesInstallerModes()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);

        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("winget", result.Output);
        Assert.Contains("homebrew", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_WinGetMode_PrDryRun_DownloadsManifestAndNativeArchives()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345",
            "--install-mode", "winget",
            "--force",
            "--dry-run",
            "--skip-extension",
            "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("winget-manifests-prerelease", result.Output);
        Assert.Contains("cli-native-archives-win-x64", result.Output);
        Assert.Contains("cli-native-archives-win-arm64", result.Output);
        Assert.Contains("-ArchiveRoot", result.Output);
        Assert.Contains("-Force", result.Output);
        Assert.Contains("built-nugets", result.Output);
        Assert.DoesNotContain("Add to your shell profile", result.Output);
        Assert.DoesNotContain("route sidecar", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dogfood/pr-12345/bin", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_HomebrewMode_PrDryRun_DownloadsCaskAndNativeArchives()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345",
            "--install-mode", "homebrew",
            "--dry-run",
            "--skip-extension",
            "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("homebrew-cask-prerelease", result.Output);
        Assert.Contains("cli-native-archives-osx-arm64", result.Output);
        Assert.Contains("cli-native-archives-osx-x64", result.Output);
        Assert.Contains("--archive-root", result.Output);
        Assert.Contains("built-nugets", result.Output);
        Assert.DoesNotContain("Add to your shell profile", result.Output);
        Assert.DoesNotContain("route sidecar", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dogfood/pr-12345/bin", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_HomebrewMode_LocalDir_DryRun_UsesDogfoodArtifact()
    {
        using var env = new TestEnvironment();
        var localDir = await CreateHomebrewInstallerArtifactAsync(Path.Combine(env.TempDirectory, "homebrew-artifact"));
        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "homebrew",
            "--dry-run",
            "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("dogfood.sh", result.Output);
        Assert.Contains("--archive-root", result.Output);
        Assert.Contains("Would copy nugets", result.Output);
        Assert.DoesNotContain("Would install CLI archive", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_InstallerMode_RejectsHiveOnly()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345",
            "--install-mode", "homebrew",
            "--hive-only",
            "--dry-run",
            "--skip-extension");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--hive-only cannot be combined with --install-mode homebrew", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_HomebrewDogfood_FailsWhenVersionCheckFails()
    {
        using var env = new TestEnvironment();
        var localDir = await CreateHomebrewInstallerArtifactAsync(Path.Combine(env.TempDirectory, "homebrew-artifact"));
        var mockBinDir = await CreateMockHomebrewBinAsync(env, aspireExitCode: 42);
        using var cmd = new ScriptToolCommand("eng/homebrew/dogfood.sh", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}{Path.PathSeparator}/usr/bin:/bin:/usr/sbin:/sbin");

        var result = await cmd.ExecuteAsync(Path.Combine(localDir, "aspire.rb"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("aspire --version failed after install", result.Output);
    }

    [Fact]
    [RequiresTools(["ruby"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_PrepareHomebrewCask_FailedVerification_UninstallsCask()
    {
        using var env = new TestEnvironment();
        var archiveRoot = Path.Combine(env.TempDirectory, "archives");
        await CreateFakeHomebrewArchivesAsync(archiveRoot);
        var mockBinDir = await CreateMockHomebrewBinAsync(env, aspireExitCode: 42);
        using var cmd = new ScriptToolCommand("eng/homebrew/prepare-cask-artifact.sh", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}{Path.PathSeparator}/usr/bin:/bin:/usr/sbin:/sbin");

        var result = await cmd.ExecuteAsync(
            "--version", "13.3.0",
            "--artifact-version", "13.3.0",
            "--channel", "stable",
            "--archive-root", archiveRoot,
            "--output-dir", Path.Combine(env.TempDirectory, "homebrew-output"));

        Assert.NotEqual(0, result.ExitCode);
        var brewLog = await File.ReadAllTextAsync(Path.Combine(env.TempDirectory, "brew.log"));
        Assert.Contains("uninstall --cask local/aspire-test/aspire", brewLog);
    }

    [Fact]
    [RequiresTools(["ruby"])]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_PrepareHomebrewCask_Offline_GeneratesCaskWithArchiveHashesAndDogfood()
    {
        using var env = new TestEnvironment();
        var archiveRoot = Path.Combine(env.TempDirectory, "archives");
        await CreateFakeHomebrewArchivesAsync(archiveRoot);
        var mockBinDir = await CreateMockHomebrewBinAsync(env, aspireExitCode: 0);
        var outputDir = Path.Combine(env.TempDirectory, "homebrew-output");
        using var cmd = new ScriptToolCommand("eng/homebrew/prepare-cask-artifact.sh", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}{Path.PathSeparator}/usr/bin:/bin:/usr/sbin:/sbin");

        var result = await cmd.ExecuteAsync(
            "--version", "13.3.0",
            "--artifact-version", "13.3.0-pr.1234.abc",
            "--channel", "prerelease",
            "--archive-root", archiveRoot,
            "--output-dir", outputDir,
            "--validation-mode", "Offline");

        result.EnsureSuccessful();

        var cask = await File.ReadAllTextAsync(Path.Combine(outputDir, "aspire.rb"));
        Assert.Contains("version \"13.3.0\"", cask);
        Assert.Contains("https://ci.dot.net/public/aspire/13.3.0-pr.1234.abc/aspire-cli-osx-#{arch}-#{version}.tar.gz", cask);
        Assert.Contains((await GetSha256HexAsync(Path.Combine(archiveRoot, "aspire-cli-osx-arm64-13.3.0.tar.gz"))).ToLowerInvariant(), cask);
        Assert.Contains((await GetSha256HexAsync(Path.Combine(archiveRoot, "aspire-cli-osx-x64-13.3.0.tar.gz"))).ToLowerInvariant(), cask);
        Assert.DoesNotContain("${", cask);
        Assert.True(File.Exists(Path.Combine(outputDir, "dogfood.sh")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PowerShell_WinGetMode_WhatIf_DownloadsManifestAndNativeArchives()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreatePsCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "-PRNumber", "12345",
            "-InstallMode", "WinGet",
            "-Force",
            "-WhatIf",
            "-SkipExtension",
            "-Verbose");

        result.EnsureSuccessful();
        Assert.Contains("winget-manifests-prerelease", result.Output);
        Assert.Contains("cli-native-archives-win-x64", result.Output);
        Assert.Contains("cli-native-archives-win-arm64", result.Output);
        Assert.Contains("-ArchiveRoot", result.Output);
        Assert.Contains("-Force", result.Output);
        Assert.Contains("built-nugets", result.Output);
        Assert.DoesNotContain("Add to your shell profile", result.Output);
        Assert.DoesNotContain("Route sidecar", result.Output);
        Assert.DoesNotContain($"dogfood{Path.DirectorySeparatorChar}pr-12345{Path.DirectorySeparatorChar}bin", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PowerShell_HomebrewMode_WhatIf_DownloadsCaskAndNativeArchives()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreatePsCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "-PRNumber", "12345",
            "-InstallMode", "Homebrew",
            "-WhatIf",
            "-SkipExtension",
            "-Verbose");

        result.EnsureSuccessful();
        Assert.Contains("homebrew-cask-prerelease", result.Output);
        Assert.Contains("cli-native-archives-osx-arm64", result.Output);
        Assert.Contains("cli-native-archives-osx-x64", result.Output);
        Assert.Contains("--archive-root", result.Output);
        Assert.Contains("built-nugets", result.Output);
        Assert.DoesNotContain("Add to your shell profile", result.Output);
        Assert.DoesNotContain("Route sidecar", result.Output);
        Assert.DoesNotContain($"dogfood{Path.DirectorySeparatorChar}pr-12345{Path.DirectorySeparatorChar}bin", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PowerShell_WinGetMode_LocalDir_WhatIf_UsesDogfoodArtifact()
    {
        using var env = new TestEnvironment();
        var localDir = await CreateWinGetInstallerArtifactAsync(Path.Combine(env.TempDirectory, "winget-artifact"));
        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-InstallMode", "WinGet",
            "-WhatIf",
            "-SkipPath");

        result.EnsureSuccessful();
        Assert.Contains("dogfood.ps1", result.Output);
        Assert.Contains("-ArchiveRoot", result.Output);
        Assert.Contains("Copying built nugets", result.Output);
        Assert.DoesNotContain("Installing Aspire CLI to", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PowerShell_InstallerMode_RejectsHiveOnly()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreatePsCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "-PRNumber", "12345",
            "-InstallMode", "Homebrew",
            "-HiveOnly",
            "-WhatIf",
            "-SkipExtension");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("-HiveOnly cannot be combined with -InstallMode Homebrew", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PowerShell_PrepareWinGetManifest_GenerateOnly_GeneratesManifestsWithArchiveHashesAndDogfood()
    {
        using var env = new TestEnvironment();
        var archiveRoot = Path.Combine(env.TempDirectory, "archives");
        await CreateFakeWinGetArchivesAsync(archiveRoot);
        var outputDir = Path.Combine(env.TempDirectory, "winget-output");
        using var cmd = new ScriptToolCommand("eng/winget/prepare-manifest-artifact.ps1", env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "-Channel", "prerelease",
            "-ArchiveRoot", archiveRoot,
            "-OutputPath", outputDir,
            "-ValidationMode", "GenerateOnly");

        result.EnsureSuccessful();

        var installerManifest = await File.ReadAllTextAsync(Path.Combine(outputDir, "Microsoft.Aspire.installer.yaml"));
        Assert.Contains("PackageVersion: \"13.3.0\"", installerManifest);
        Assert.Contains("InstallerUrl: https://ci.dot.net/public/aspire/13.3.0/aspire-cli-win-x64-13.3.0.zip", installerManifest);
        Assert.Contains("InstallerUrl: https://ci.dot.net/public/aspire/13.3.0/aspire-cli-win-arm64-13.3.0.zip", installerManifest);
        Assert.Contains(await GetSha256HexAsync(Path.Combine(archiveRoot, "Debug", "Shipping", "aspire-cli-win-x64-13.3.0.zip")), installerManifest);
        Assert.Contains(await GetSha256HexAsync(Path.Combine(archiveRoot, "Debug", "Shipping", "aspire-cli-win-arm64-13.3.0.zip")), installerManifest);
        Assert.DoesNotContain(new string('0', 64), installerManifest);
        Assert.DoesNotContain("${", installerManifest);

        var localeManifest = await File.ReadAllTextAsync(Path.Combine(outputDir, "Microsoft.Aspire.locale.en-US.yaml"));
        Assert.Contains("For testing builds only. Prerelease package in stable manifest.", localeManifest);
        Assert.True(File.Exists(Path.Combine(outputDir, "Microsoft.Aspire.yaml")));
        Assert.True(File.Exists(Path.Combine(outputDir, "dogfood.ps1")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "winget is Windows-only")]
    public async Task PowerShell_WinGetDogfood_Force_PassesForceToWingetInstall()
    {
        using var env = new TestEnvironment();
        var localDir = await CreateWinGetInstallerArtifactAsync(Path.Combine(env.TempDirectory, "winget-artifact"));
        var mockBinDir = await CreateMockWinGetBinAsync(env, aspireExitCode: 0);
        using var cmd = new ScriptToolCommand("eng/winget/dogfood.ps1", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

        var result = await cmd.ExecuteAsync("-ManifestPath", localDir, "-Force");

        result.EnsureSuccessful();
        var wingetLog = await File.ReadAllTextAsync(Path.Combine(env.TempDirectory, "winget.log"));
        Assert.Contains("install --manifest", wingetLog);
        Assert.Contains("--force", wingetLog);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "winget is Windows-only")]
    public async Task PowerShell_WinGetDogfood_FailsWhenVersionCheckFails()
    {
        using var env = new TestEnvironment();
        var localDir = await CreateWinGetInstallerArtifactAsync(Path.Combine(env.TempDirectory, "winget-artifact"));
        var mockBinDir = await CreateMockWinGetBinAsync(env, aspireExitCode: 42);
        using var cmd = new ScriptToolCommand("eng/winget/dogfood.ps1", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

        var result = await cmd.ExecuteAsync("-ManifestPath", localDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Failed to verify Aspire CLI installation", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "winget is Windows-only")]
    public async Task PowerShell_WinGetDogfood_ArchiveRoot_ValidatesPristineAndInstallsRewrittenManifest()
    {
        // This mirrors get-aspire-cli-pr.ps1 -InstallMode WinGet's invocation of
        // dogfood.ps1 -ArchiveRoot. End-to-end behaviours that have to hold:
        //   1. ``winget validate`` runs against the pristine https:// URLs (the schema
        //      rejects anything that's not ^https?://).
        //   2. ``Set-LocalInstallerSources`` rewrites InstallerUrl to point at the
        //      loopback HTTP listener (not file:// — winget's WinINet-based downloader
        //      rejects file:// with HRESULT 0x8007007b) and refreshes InstallerSha256
        //      (PR-channel manifests ship with a placeholder of all zeros).
        //   3. The mock then actually downloads the URL via Invoke-WebRequest and
        //      hashes the bytes, which exercises both points 1 and 2 end-to-end.
        using var env = new TestEnvironment();
        var (manifestDir, archiveRoot) = await CreateWinGetPrChannelArtifactAsync(env.TempDirectory);
        var mockBinDir = await CreateMockWinGetBinAsync(env, aspireExitCode: 0);
        using var cmd = new ScriptToolCommand("eng/winget/dogfood.ps1", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

        var result = await cmd.ExecuteAsync("-ManifestPath", manifestDir, "-ArchiveRoot", archiveRoot);

        result.EnsureSuccessful();

        var wingetLog = await File.ReadAllTextAsync(Path.Combine(env.TempDirectory, "winget.log"));
        Assert.Contains("validate --manifest", wingetLog);
        Assert.Contains("install --manifest", wingetLog);

        // The pristine manifest in the artifact directory must be untouched (re-runnable).
        var originalInstaller = await File.ReadAllTextAsync(Path.Combine(manifestDir, "Microsoft.Aspire.installer.yaml"));
        Assert.Contains("https://ci.dot.net/", originalInstaller);
        Assert.Contains(new string('0', 64), originalInstaller);
        Assert.DoesNotContain("file://", originalInstaller);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "winget is Windows-only")]
    public async Task PowerShell_WinGetDogfood_ArchiveRoot_FailsWhenArchiveBytesChange()
    {
        // Guard against silent regressions: if Set-LocalInstallerSources ever stops
        // refreshing InstallerSha256, the mock winget install will download the
        // archive over the loopback listener and detect the hash mismatch.
        using var env = new TestEnvironment();
        var (manifestDir, archiveRoot) = await CreateWinGetPrChannelArtifactAsync(env.TempDirectory);

        // Tamper the archive *after* the manifest's placeholder hash was written. The
        // refreshed hash must reflect the new bytes for ``winget install`` to succeed.
        // We replace with a valid zip (still extractable, still satisfies the
        // NestedInstallerFiles contract) but with different stub contents — so only
        // the hash refresh is being exercised, not the extraction layout check.
        var x64Archive = Path.Combine(archiveRoot, "Debug", "Shipping", "aspire-cli-win-x64-13.3.0.zip");
        await WriteRealAspireWinGetZipAsync(x64Archive, "post-generate-mutated-x64-bytes");

        var mockBinDir = await CreateMockWinGetBinAsync(env, aspireExitCode: 0);
        using var cmd = new ScriptToolCommand("eng/winget/dogfood.ps1", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

        var result = await cmd.ExecuteAsync("-ManifestPath", manifestDir, "-ArchiveRoot", archiveRoot);

        result.EnsureSuccessful();
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PowerShell_PrepareWinGetManifest_NestedInstallerFiles_MatchArchiveContents()
    {
        // Manifest-vs-archive contract: real winget extracts InstallerUrl's zip at
        // install time and looks for every NestedInstallerFiles[].RelativeFilePath in
        // the extracted contents. If the prepare-manifest-artifact.ps1 template ever
        // drifts away from the actual archive layout — wrong RelativeFilePath, wrong
        // CWD, missing file — real winget fails at install time with an opaque error
        // (we observed 0x8A150001 with no diag-log line beyond "Started applying motw").
        //
        // This test runs the real prepare script against real zips and checks the
        // contract holds end-to-end without needing winget itself, so it can run on
        // Linux CI as a fast deterministic gate.
        using var env = new TestEnvironment();
        var archiveRoot = Path.Combine(env.TempDirectory, "archives");
        var archiveDir = Path.Combine(archiveRoot, "Debug", "Shipping");
        Directory.CreateDirectory(archiveDir);
        var x64Zip = Path.Combine(archiveDir, "aspire-cli-win-x64-13.3.0.zip");
        var arm64Zip = Path.Combine(archiveDir, "aspire-cli-win-arm64-13.3.0.zip");
        await WriteRealAspireWinGetZipAsync(x64Zip, "stub-x64-13.3.0");
        await WriteRealAspireWinGetZipAsync(arm64Zip, "stub-arm64-13.3.0");

        var outputDir = Path.Combine(env.TempDirectory, "winget-output");
        using var cmd = new ScriptToolCommand("eng/winget/prepare-manifest-artifact.ps1", env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "-Channel", "prerelease",
            "-ArchiveRoot", archiveRoot,
            "-OutputPath", outputDir,
            "-ValidationMode", "GenerateOnly");

        result.EnsureSuccessful();

        var installerYaml = await File.ReadAllTextAsync(Path.Combine(outputDir, "Microsoft.Aspire.installer.yaml"));
        var relativeFilePaths = ParseRelativeFilePaths(installerYaml);
        Assert.NotEmpty(relativeFilePaths);

        foreach (var archive in new[] { x64Zip, arm64Zip })
        {
            var extractDir = Path.Combine(env.TempDirectory, $"extract-{Path.GetFileNameWithoutExtension(archive)}");
            System.IO.Compression.ZipFile.ExtractToDirectory(archive, extractDir);
            foreach (var rel in relativeFilePaths)
            {
                var expected = Path.Combine(extractDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(expected),
                    $"NestedInstallerFiles entry '{rel}' from {Path.GetFileName(archive)} not found at {expected}. Manifest disagrees with archive contents.");
            }
        }
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "winget is Windows-only")]
    public async Task PowerShell_WinGetDogfood_ArchiveRoot_FailsWhenNestedInstallerFileMissingFromArchive()
    {
        // Companion to PowerShell_PrepareWinGetManifest_NestedInstallerFiles_MatchArchiveContents:
        // exercises the same contract from the install side. The manifest declares
        // NestedInstallerFiles: [RelativeFilePath: aspire.exe] but the served zip is
        // rebuilt with only a sentinel file, no aspire.exe. Real winget would fail at
        // install time with an opaque error (after the InstallerSha256 verify step);
        // the mock catches the same contract violation deterministically with an
        // attributable error message.
        using var env = new TestEnvironment();
        var (manifestDir, archiveRoot) = await CreateWinGetPrChannelArtifactAsync(env.TempDirectory);

        var x64Archive = Path.Combine(archiveRoot, "Debug", "Shipping", "aspire-cli-win-x64-13.3.0.zip");
        File.Delete(x64Archive);
        await using (var fs = File.Create(x64Archive))
        using (var archive = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("not-aspire.exe", System.IO.Compression.CompressionLevel.NoCompression);
            await using var s = entry.Open();
            await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes("sentinel"));
        }

        var mockBinDir = await CreateMockWinGetBinAsync(env, aspireExitCode: 0);
        using var cmd = new ScriptToolCommand("eng/winget/dogfood.ps1", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");

        var result = await cmd.ExecuteAsync("-ManifestPath", manifestDir, "-ArchiveRoot", archiveRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("NestedInstallerFiles entry 'aspire.exe' is missing", result.Output);
    }
}
