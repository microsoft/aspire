// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using Aspire.SelectTests;
using Aspire.Tools.CreateLayout;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class TrayBundleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    public void LinuxLayoutsRequireExplicitNativePayload(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = new LayoutBuilder(workspace.Path, workspace.Path, rid, "Debug", "test", false, null, null, null);
        Assert.Equal("The Linux bundle requires --tray-linux pointing to the pre-built native tray publish directory.",
            Assert.Throws<InvalidOperationException>(builder.CopyTray).Message);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [UnsupportedOSPlatform("windows")]
    public async Task LinuxPayloadCopyAndArchivePreserveOnlyNativeRuntimeFiles(string rid)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux bundles preserve Unix modes.");
        using var workspace = TemporaryWorkspace.Create(output);
        var source = Path.Combine(workspace.Path, "publish");
        LinuxTrayTestPayload.Create(source, rid);
        File.WriteAllText(Path.Combine(source, "aspire-tray.dbg"), "symbols");
        var layout = Path.Combine(workspace.Path, rid);
        using var builder = new LayoutBuilder(layout, workspace.Path, rid, "Debug", "test", false, null, null, source);
        builder.CopyTray();
        var archivePath = await builder.CreateArchiveAsync();
        await using var archive = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        List<string> names = [];
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }
            names.Add(entry.Name);
            var original = Path.Combine(source, Path.GetFileName(entry.Name));
            Assert.Equal(File.GetUnixFileMode(original), entry.Mode);
            using var bytes = new MemoryStream();
            await entry.DataStream!.CopyToAsync(bytes);
            Assert.Equal(File.ReadAllBytes(original), bytes.ToArray());
        }
        Assert.Equal(new[] { $"{rid}/tray/Aspire.png", $"{rid}/tray/aspire-tray" }.Order(), names.Order());
    }

    [Theory]
    [InlineData("linux-musl-x64")]
    public void MuslLayoutsDoNotPackageTray(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = new LayoutBuilder(workspace.Path, workspace.Path, rid, "Debug", "test", false, "missing.app", "missing-windows", "missing-linux");

        builder.CopyTray();

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Path));
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public void MacLayoutRequiresExplicitTrayInput(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = new LayoutBuilder(workspace.Path, workspace.Path, rid, "Debug", "test", false, null, null, null);

        var error = Assert.Throws<InvalidOperationException>(builder.CopyTray);

        Assert.Contains("--tray-app", error.Message);
    }

    [Theory]
    [InlineData("Contents/MacOS/aspire-tray")]
    [InlineData("Contents/Info.plist")]
    [InlineData("Contents/Resources/Aspire.icns")]
    [InlineData("Contents/_CodeSignature/CodeResources")]
    public void MacLayoutRejectsIncompleteTray(string missingFile)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = MacTrayTestPayload.Create(workspace.Path);
        File.Delete(Path.Combine(source, missingFile));
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, "osx-arm64", "Debug", "test", false, source, null, null);

        var error = Assert.Throws<InvalidOperationException>(builder.CopyTray);

        Assert.Contains(Path.Combine(source, missingFile), error.Message);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void MacLayoutRejectsNonExecutableTray()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix executable mode check.");
        using var workspace = TemporaryWorkspace.Create(output);
        var source = MacTrayTestPayload.Create(workspace.Path);
        File.SetUnixFileMode(Path.Combine(source, "Contents/MacOS/aspire-tray"), UnixFileMode.UserRead);
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, "osx-arm64", "Debug", "test", false, source, null, null);

        var error = Assert.Throws<InvalidOperationException>(builder.CopyTray);

        Assert.Contains("not executable", error.Message);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task AppResourcesSignaturesAndExecutableModesSurviveCopyAndArchive()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS bundles are assembled on Unix.");
        using var workspace = TemporaryWorkspace.Create(output);
        var source = MacTrayTestPayload.Create(workspace.Path);
        var layout = Path.Combine(workspace.Path, "osx-arm64");
        var destination = Path.Combine(layout, "tray", "Aspire Tray.app");
        using var builder = new LayoutBuilder(layout, workspace.Path, "osx-arm64", "Debug", "test", false, source, null, null);

        builder.CopyTray();

        var expectedFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(source, file)).Order().ToArray();
        Assert.Equal(expectedFiles, Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(destination, file)).Order().ToArray());
        foreach (var file in expectedFiles)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, file)), File.ReadAllBytes(Path.Combine(destination, file)));
            Assert.Equal(File.GetUnixFileMode(Path.Combine(source, file)), File.GetUnixFileMode(Path.Combine(destination, file)));
        }

        var archivePath = await builder.CreateArchiveAsync();
        await using var archive = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        var archivedFiles = new List<string>();
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }
            const string prefix = "osx-arm64/tray/Aspire Tray.app/";
            Assert.StartsWith(prefix, entry.Name);
            var relativePath = entry.Name[prefix.Length..];
            archivedFiles.Add(relativePath);
            Assert.Equal(File.GetUnixFileMode(Path.Combine(source, relativePath)), entry.Mode);
            using var content = new MemoryStream();
            await entry.DataStream!.CopyToAsync(content);
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, relativePath)), content.ToArray());
        }

        Assert.Equal(expectedFiles, archivedFiles.Order());
    }

    [Fact]
    [RequiresTools(["pwsh", "ditto"])]
    [SupportedOSPlatform("macos")]
    public async Task MacSigningTransportContainsAppDirectoryAndPreservesExecutableModes()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS signing transport uses ditto.");
        using var workspace = TemporaryWorkspace.Create(output);
        var source = MacTrayTestPayload.Create(workspace.Path);
        var archivePath = Path.Combine(workspace.Path, "signing", "AspireTray.zip");
        var scriptPath = Path.Combine(workspace.Path, "prepare-signing.ps1");
        File.WriteAllText(scriptPath, """
            param([string]$RepositoryRoot, [string]$AppPath, [string]$ArchivePath)
            & "$RepositoryRoot/dotnet.sh" msbuild "$RepositoryRoot/src/Aspire.Tray/Mac/Aspire.Tray.Mac.csproj" `
                /t:PrepareTraySigning /p:RuntimeIdentifier=osx-arm64 `
                "/p:TrayAppPath=$AppPath" "/p:TraySigningArchive=$ArchivePath" /nologo
            exit $LASTEXITCODE
            """);
        using var command = new PowerShellCommand(scriptPath, output)
            .WithWorkingDirectory(RepoRoot.Path)
            .WithTimeout(TimeSpan.FromMinutes(2));

        var result = await command.ExecuteAsync(
            "-RepositoryRoot", $"\"{RepoRoot.Path}\"",
            "-AppPath", $"\"{source}\"",
            "-ArchivePath", $"\"{archivePath}\"");

        result.EnsureSuccessful();
        using var archive = ZipFile.OpenRead(archivePath);
        var files = archive.Entries.Where(entry => entry.Name.Length > 0 && !entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)).ToArray();
        var expectedFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Select(file => $"{Path.GetFileName(source)}/{Path.GetRelativePath(source, file)}").Order().ToArray();
        Assert.Equal(expectedFiles, files.Select(entry => entry.FullName).Order());
        foreach (var entry in files)
        {
            var file = Path.Combine(Path.GetDirectoryName(source)!, entry.FullName);
            using var content = new MemoryStream();
            using var entryStream = entry.Open();
            await entryStream.CopyToAsync(content);
            Assert.Equal(File.ReadAllBytes(file), content.ToArray());
            Assert.Equal(File.GetUnixFileMode(file), (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF));
        }
    }

    [Theory]
    [InlineData("Aspire.Tray.Mac", true)]
    [InlineData("Aspire.Tray.Windows", true)]
    [InlineData("Aspire.Tray.Linux", true)]
    public void NativeTrayChangesSelectSharedAndPackagingTests(string project, bool bundled)
    {
        var selector = new TestSelector(
            Path.Combine(RepoRoot.Path, "eng/github-ci/test-trigger-map.yml"),
            new HashSet<string>(["Aspire.Tray.Tests", "Infrastructure.Tests", "Aspire.Cli.EndToEnd.Tests"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            affectedTestProjectNames: new HashSet<string>(StringComparer.Ordinal));

        var result = selector.Select([], [project], new SelectorOptions());

        Assert.False(result.SelectsAll);
        Assert.Equal(bundled
                ? ["Aspire.Cli.EndToEnd.Tests", "Aspire.Tray.Tests", "Infrastructure.Tests"]
                : new[] { "Aspire.Tray.Tests", "Infrastructure.Tests" },
            result.TestProjects.Order(StringComparer.Ordinal));
        Assert.Equal(bundled
                ? ["job:cli-starter-validation", "job:extension-e2e", "job:homebrew-installer", "job:winget-installer"]
                : Array.Empty<string>(),
            result.Jobs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public async Task WindowsLayoutCopiesOnlyNativePayloadAndPreservesArchiveBytes(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = Path.Combine(workspace.Path, "publish with spaces");
        WindowsTrayTestPayload.Create(source, rid);
        File.WriteAllText(Path.Combine(source, "smoke.stdout.log"), "not runtime content");
        var layout = Path.Combine(workspace.Path, rid);
        using var builder = new LayoutBuilder(layout, workspace.Path, rid, "Debug", "test", false, null, source, null);

        builder.CopyTray();

        var archivePath = await builder.CreateArchiveAsync();
        await using var archive = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        var files = new List<string>();
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }
            files.Add(entry.Name);
            using var content = new MemoryStream();
            await entry.DataStream!.CopyToAsync(content);
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, Path.GetFileName(entry.Name))), content.ToArray());
        }
        Assert.Equal(new[] { $"{rid}/tray/Aspire.ico", $"{rid}/tray/aspire-tray.exe" }.Order(), files.Order());
    }

    [Theory]
    [InlineData("missing-input")]
    [InlineData("missing-icon")]
    [InlineData("missing-executable")]
    [InlineData("wrong-rid")]
    [InlineData("unsupported-rid")]
    [InlineData("managed")]
    public void WindowsLayoutRejectsInvalidPublishOutput(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = Path.Combine(workspace.Path, "publish");
        WindowsTrayTestPayload.Create(source, scenario == "wrong-rid" ? "win-arm64" : "win-x64");
        if (scenario == "missing-icon")
        {
            File.Delete(Path.Combine(source, "Aspire.ico"));
        }
        if (scenario == "missing-executable")
        {
            File.Delete(Path.Combine(source, "aspire-tray.exe"));
        }
        if (scenario == "managed")
        {
            File.Copy(typeof(TrayBundleTests).Assembly.Location, Path.Combine(source, "aspire-tray.exe"), overwrite: true);
        }
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path,
            scenario == "unsupported-rid" ? "win-x86" : "win-x64", "Debug", "test", false, null,
            scenario == "missing-input" ? null : source, null);

        if (scenario == "missing-input")
        {
            Assert.Equal("The Windows bundle requires --tray-windows pointing to the pre-built native tray publish directory.",
                Assert.Throws<InvalidOperationException>(builder.CopyTray).Message);
        }
        else
        {
            Assert.Throws<InvalidDataException>(builder.CopyTray);
        }
    }

    [Theory]
    [RequiresTools(["pwsh", "tar"])]
    [InlineData("win-x64", "valid", 0)]
    [InlineData("win-arm64", "valid", 0)]
    [InlineData("win-x64", "wrong-rid", 1)]
    [InlineData("win-x64", "missing-icon", 1)]
    [InlineData("win-x64", "changed-icon", 1)]
    [InlineData("win-x64", "extra-file", 1)]
    [InlineData("win-x64", "managed", 1)]
    public async Task WindowsArchiveVerifierChecksActualPayload(string rid, string scenario, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = Path.Combine(workspace.Path, "source");
        WindowsTrayTestPayload.Create(source, rid);
        File.Copy(Path.Combine(RepoRoot.Path, "src/Shared/Aspire.ico"), Path.Combine(source, "Aspire.ico"), overwrite: true);
        var layout = Path.Combine(workspace.Path, rid);
        using var builder = new LayoutBuilder(layout, workspace.Path, rid, "Debug", "test", false, null, source, null);
        builder.CopyTray();
        var tray = Path.Combine(layout, "tray");
        switch (scenario)
        {
            case "wrong-rid":
                WindowsTrayTestPayload.Create(tray, "win-arm64");
                File.Copy(Path.Combine(source, "Aspire.ico"), Path.Combine(tray, "Aspire.ico"), overwrite: true);
                break;
            case "missing-icon":
                File.Delete(Path.Combine(tray, "Aspire.ico"));
                break;
            case "changed-icon":
                File.WriteAllText(Path.Combine(tray, "Aspire.ico"), "wrong icon");
                break;
            case "extra-file":
                File.WriteAllText(Path.Combine(tray, "smoke.log"), "unexpected runtime content");
                break;
            case "managed":
                File.Copy(typeof(TrayBundleTests).Assembly.Location, Path.Combine(tray, "aspire-tray.exe"), overwrite: true);
                break;
        }
        var archive = await builder.CreateArchiveAsync();
        using var command = new PowerShellCommand(Path.Combine(RepoRoot.Path, "tools/CreateLayout/verify-windows-tray-payload.ps1"), output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        var result = await command.ExecuteAsync("-Archive", $"\"{archive}\"", "-Rid", rid, "-RequireSignature", "false");

        result.EnsureExitCode(expectedExitCode);
        if (expectedExitCode == 0)
        {
            Assert.Contains($"Verified {rid} Windows tray payload", result.Output);
        }
    }

    [Theory]
    [RequiresTools(["pwsh", "bash", "tar", "find", "od"])]
    [UnsupportedOSPlatform("windows")]
    [InlineData("linux-x64", "valid", 0)]
    [InlineData("linux-arm64", "valid", 0)]
    [InlineData("linux-x64", "wrong-rid", 1)]
    [InlineData("linux-x64", "missing-icon", 1)]
    [InlineData("linux-x64", "changed-icon", 1)]
    [InlineData("linux-x64", "non-executable", 1)]
    [InlineData("linux-x64", "extra-file", 1)]
    [InlineData("linux-x64", "extra-directory", 1)]
    [InlineData("linux-x64", "symlink-icon", 1)]
    [InlineData("linux-x64", "symlink-executable", 1)]
    public async Task LinuxArchiveVerifierChecksActualPayload(string rid, string scenario, int expectedExitCode)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux bundles preserve Unix modes.");
        using var workspace = TemporaryWorkspace.Create(output);
        var source = Path.Combine(workspace.Path, "publish with spaces");
        LinuxTrayTestPayload.Create(source, rid);
        var layout = Path.Combine(workspace.Path, rid);
        using var builder = new LayoutBuilder(layout, workspace.Path, rid, "Debug", "test", false, null, null, source);
        builder.CopyTray();
        var tray = Path.Combine(layout, "tray");
        switch (scenario)
        {
            case "wrong-rid":
                LinuxTrayTestPayload.Create(tray, "linux-arm64");
                break;
            case "missing-icon":
                File.Delete(Path.Combine(tray, "Aspire.png"));
                break;
            case "changed-icon":
                File.WriteAllText(Path.Combine(tray, "Aspire.png"), "wrong icon");
                break;
            case "non-executable":
                File.SetUnixFileMode(Path.Combine(tray, "aspire-tray"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                break;
            case "extra-file":
                File.WriteAllText(Path.Combine(tray, "smoke.log"), "unexpected runtime content");
                break;
            case "extra-directory":
                Directory.CreateDirectory(Path.Combine(tray, "empty"));
                break;
            case "symlink-icon":
            case "symlink-executable":
                var name = scenario == "symlink-icon" ? "Aspire.png" : "aspire-tray";
                File.Delete(Path.Combine(tray, name));
                File.CreateSymbolicLink(Path.Combine(tray, name), Path.Combine(source, name));
                break;
        }
        var archive = await builder.CreateArchiveAsync();
        var script = Path.Combine(workspace.Path, "verify.ps1");
        File.WriteAllText(script, """
            param([string]$Verifier, [string]$Archive, [string]$Rid)
            & bash $Verifier $Archive $Rid
            exit $LASTEXITCODE
            """);
        using var command = new PowerShellCommand(script, output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        var result = await command.ExecuteAsync(
            "-Verifier", $"\"{Path.Combine(RepoRoot.Path, "tools/CreateLayout/verify-linux-tray-payload.sh")}\"",
            "-Archive", $"\"{archive}\"", "-Rid", rid);

        result.EnsureExitCode(expectedExitCode);
        if (expectedExitCode == 0)
        {
            Assert.Contains($"Verified Linux tray payload for {rid}.", result.Output);
        }
    }

    [Theory]
    [RequiresTools(["pwsh", "tar"])]
    [UnsupportedOSPlatform("windows")]
    [InlineData("valid", 0, "Packaged tray launchability check passed")]
    [InlineData("verbose", 0, "Packaged tray launchability check passed")]
    [InlineData("missing-executable", 1, "Packaged tray executable was not found")]
    [InlineData("non-executable", 1, "Permission denied")]
    [InlineData("nonzero-exit", 1, "failed with exit code 7")]
    [InlineData("timeout", 1, "timed out after 1 seconds")]
    [InlineData("invalid-archive", 1, "Failed to extract the packaged tray")]
    public async Task NativeTrayLaunchCheckReportsFailuresWithoutRepairingPayload(string scenario, int expectedExitCode, string expectedMessage)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Test executable uses a Unix shell.");
        using var workspace = TemporaryWorkspace.Create(output);
        var layout = Path.Combine(workspace.Path, "linux-x64");
        var tray = Path.Combine(layout, "tray");
        Directory.CreateDirectory(tray);
        var executable = Path.Combine(tray, "aspire-tray");
        File.WriteAllText(executable, scenario switch
        {
            "nonzero-exit" => "#!/bin/sh\nexit 7\n",
            "timeout" => "#!/bin/sh\nexec sleep 60\n",
            "verbose" => """
                #!/bin/sh
                i=0
                while [ "$i" -lt 10000 ]; do
                    printf 'tray stdout\n'
                    printf 'tray stderr\n' >&2
                    i=$((i + 1))
                done
                """ + "\n",
            _ => "#!/bin/sh\n[ \"$#\" -eq 1 ] && [ \"$1\" = '--help' ]\n"
        });
        File.SetUnixFileMode(executable, scenario == "non-executable"
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (scenario == "missing-executable")
        {
            File.Delete(executable);
        }
        using var builder = new LayoutBuilder(layout, workspace.Path, "linux-x64", "Debug", "test", false, null, null, null);
        var archive = await builder.CreateArchiveAsync();
        if (scenario == "invalid-archive")
        {
            File.WriteAllText(archive, "not a tar archive");
        }
        using var command = new PowerShellCommand(Path.Combine(RepoRoot.Path, "eng/scripts/test-native-tray.ps1"), output)
            .WithTimeout(TimeSpan.FromMinutes(1));

        var result = await command.ExecuteAsync("-ArchivePath", $"\"{archive}\"", "-Rid", "linux-x64",
            "-TimeoutSeconds", scenario == "timeout" ? "1" : "30");

        result.EnsureExitCode(expectedExitCode);
        Assert.Contains(expectedMessage, result.Output);
    }
}
