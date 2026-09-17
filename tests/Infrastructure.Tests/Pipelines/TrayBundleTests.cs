// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Xml.Linq;
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
    public void LinuxLayoutsDoNotPackageTray(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = new LayoutBuilder(workspace.Path, workspace.Path, rid, "test", false, "missing.app", "missing-windows");

        builder.CopyTray();

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Path));
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public void MacLayoutRequiresExplicitTrayInput(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = new LayoutBuilder(workspace.Path, workspace.Path, rid, "test", false, null, null);

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
        var source = CreateApp(workspace.Path);
        File.Delete(Path.Combine(source, missingFile));
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, "osx-arm64", "test", false, source, null);

        var error = Assert.Throws<InvalidOperationException>(builder.CopyTray);

        Assert.Contains(Path.Combine(source, missingFile), error.Message);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void MacLayoutRejectsNonExecutableTray()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix executable mode check.");
        using var workspace = TemporaryWorkspace.Create(output);
        var source = CreateApp(workspace.Path);
        File.SetUnixFileMode(Path.Combine(source, "Contents/MacOS/aspire-tray"), UnixFileMode.UserRead);
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, "osx-arm64", "test", false, source, null);

        var error = Assert.Throws<InvalidOperationException>(builder.CopyTray);

        Assert.Contains("not executable", error.Message);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task AppResourcesSignaturesAndExecutableModesSurviveCopyAndArchive()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "macOS bundles are assembled on Unix.");
        using var workspace = TemporaryWorkspace.Create(output);
        var source = CreateApp(workspace.Path);
        var layout = Path.Combine(workspace.Path, "osx-arm64");
        var destination = Path.Combine(layout, "tray", "Aspire Tray.app");
        using var builder = new LayoutBuilder(layout, workspace.Path, "osx-arm64", "test", false, source, null);

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
    public void BundlePublishesTrayBeforeLayoutIndependentlyOfNativeCliSkip()
    {
        var project = LoadProject("eng/Bundle.proj");
        var build = Target(project, "Build");
        var dependencies = build.Attribute("DependsOnTargets")!.Value.Split(';', StringSplitOptions.TrimEntries);
        Assert.True(Array.IndexOf(dependencies, "_PublishNativeTray") < Array.IndexOf(dependencies, "_RunCreateLayout"));
        Assert.Equal("($(TargetRid.StartsWith('osx-')) or $(TargetRid.StartsWith('win-'))) and '$(SkipTrayBuild)' != 'true'", Target(project, "_PublishNativeTray").Attribute("Condition")!.Value);
        Assert.Equal("'$(SkipNativeBuild)' != 'true'", Target(project, "_PublishNativeCli").Attribute("Condition")!.Value);
        Assert.Contains("--tray-app \"$(TrayAppPath)\"", Target(project, "_RunCreateLayout").Value);
        Assert.Contains("--tray-windows \"$(WindowsTrayPath)\"", Target(project, "_RunCreateLayout").Value);
        var cliPublish = Target(project, "_PublishNativeCli");
        Assert.Equal("--output \"$(CliPublishDir)\"", Assert.Single(cliPublish.Descendants("_CliPublishOutputArg")).Value);
        Assert.Contains("$(_CliPublishOutputArg)", Assert.Single(cliPublish.Elements("Exec")).Attribute("Command")!.Value);
    }

    [Fact]
    public void SigningUsesWholeAppAndRestoresItWithoutPublishing()
    {
        var project = LoadProject("src/Aspire.Tray/Mac/Aspire.Tray.Mac.csproj");
        Assert.Equal("Publish", Target(project, "PackageTray").Attribute("AfterTargets")!.Value);
        var restore = Target(project, "RestoreSignedTray");
        Assert.Null(restore.Attribute("DependsOnTargets"));
        Assert.Contains(restore.Elements("Exec"), exec => exec.Attribute("Command")!.Value.StartsWith("ditto -xk", StringComparison.Ordinal));
        Assert.Contains(restore.Elements("Exec"), exec => exec.Attribute("Command")!.Value.Contains("anchor apple generic", StringComparison.Ordinal));

        var signing = LoadProject("eng/Signing.props");
        var rule = Assert.Single(signing.Descendants("FileSignInfo"), element => element.Attribute("Include")?.Value == "AspireTray.app");
        Assert.Equal("MacDeveloperHardenWithNotarization", rule.Attribute("CertificateName")!.Value);
        Assert.Contains(signing.Descendants("ItemsToSign"), element => element.Attribute("Include")?.Value == "$(ArtifactsBinDir)Aspire.Tray.Mac/**/signing/AspireTray.app");

        var pipeline = File.ReadAllText(Path.Combine(RepoRoot.Path, "eng/pipelines/templates/build_sign_native.yml"));
        var prepare = pipeline.IndexOf("/t:PrepareTraySigning", StringComparison.Ordinal);
        var sign = pipeline.IndexOf("SignManaged.binlog", StringComparison.Ordinal);
        var restoreApp = pipeline.IndexOf("/t:RestoreSignedTray", StringComparison.Ordinal);
        var layout = pipeline.IndexOf("/t:_RestoreDcpPackage;_RunCreateLayout", StringComparison.Ordinal);
        Assert.True(prepare >= 0 && prepare < sign && sign < restoreApp && restoreApp < layout);
        Assert.Contains("/p:SkipTrayBuild=true", pipeline);
    }

    [Fact]
    public void NativePayloadVerificationRunsAfterLayoutAndSharedTestsUseCentralMatrix()
    {
        var project = LoadProject("eng/Bundle.proj");
        var verification = Target(project, "_VerifyNativeTrayArchive");
        Assert.Equal("_RunCreateLayout", verification.Attribute("AfterTargets")!.Value);
        Assert.Equal("$(TargetRid.StartsWith('osx-'))", verification.Attribute("Condition")!.Value);
        var command = Assert.Single(verification.Elements("Exec")).Attribute("Command")!.Value;
        Assert.Contains("verify-tray-payload.sh", command);
        Assert.Contains("aspire-$(BundleVersion)-$(TargetRid).tar.gz", command);

        const string testProjectPath = "tests/Aspire.Tray.Tests/Aspire.Tray.Tests.csproj";
        var solution = LoadProject("Aspire.slnx");
        Assert.Contains(solution.Descendants("Project"), entry => entry.Attribute("Path")?.Value == testProjectPath);
        var testProject = LoadProject(testProjectPath);
        Assert.Equal("true", Assert.Single(testProject.Descendants("RunOnGithubActionsMacOS")).Value);
    }

    [Theory]
    [InlineData("Aspire.Tray.Mac", true)]
    [InlineData("Aspire.Tray.Windows", true)]
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

    [Fact]
    public void BundleUsesProductTrayProjectAndOutputDirectory()
    {
        var project = LoadProject("eng/Bundle.proj");
        Assert.Equal(@"$(RepoRoot)src\Aspire.Tray\Mac\Aspire.Tray.Mac.csproj", Assert.Single(project.Descendants("TrayProjectPath")).Value);
        Assert.Equal(@"$(ArtifactsDir)bin\Aspire.Tray.Mac\$(Configuration)\net10.0\$(TargetRid)\app\Aspire Tray.app", Assert.Single(project.Descendants("TrayAppPath")).Value);
        Assert.All(Target(project, "_PublishNativeTray").Elements("Exec"),
            exec => Assert.Contains("$(_OfficialBuildIdArg)", exec.Attribute("Command")!.Value));
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
        using var builder = new LayoutBuilder(layout, workspace.Path, rid, "test", false, null, source);

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
            scenario == "unsupported-rid" ? "win-x86" : "win-x64", "test", false, null,
            scenario == "missing-input" ? null : source);

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

    [Fact]
    public void WindowsSigningAndArchiveVerificationUsePublishedPayloadBeforeEmbedding()
    {
        var signing = LoadProject("eng/Signing.props");
        Assert.Equal("Microsoft400", Assert.Single(signing.Descendants("FileSignInfo"),
            element => element.Attribute("Include")?.Value == "aspire-tray.exe").Attribute("CertificateName")!.Value);
        Assert.Contains(signing.Descendants("ItemsToSign"),
            element => element.Attribute("Include")?.Value == @"$(ArtifactsBinDir)Aspire.Tray.Windows\**\publish\aspire-tray.exe");
        var bundle = LoadProject("eng/Bundle.proj");
        var verify = Target(bundle, "_VerifyWindowsTrayArchive");
        Assert.Equal("_RunCreateLayout", verify.Attribute("AfterTargets")!.Value);
        Assert.Equal("$(TargetRid.StartsWith('win-'))", verify.Attribute("Condition")!.Value);
        Assert.All(verify.Elements("Exec"),
            exec => Assert.Contains("verify-windows-tray-payload.ps1", exec.Attribute("Command")!.Value));
        var pipeline = File.ReadAllText(Path.Combine(RepoRoot.Path, "eng/pipelines/templates/build_sign_native.yml"));
        Assert.Contains("if in(parameters.agentOs, 'macos', 'windows')", pipeline);
        Assert.Contains("/p:RequireWindowsTraySignature=${{ parameters.codeSign }}", pipeline);
        Assert.True(pipeline.IndexOf("/t:_PublishNativeTray", StringComparison.Ordinal) <
            pipeline.IndexOf("SignManaged.binlog", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowsNativeSmokeUsesMatchingPublishedCliAndExplicitBoundedDuration()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github/workflows/build-cli-native-archives.yml"));
        Assert.Contains("- name: Check Windows tray desktop availability\n        id: windows_tray_desktop\n        if: runner.os == 'Windows'", workflow);
        Assert.Contains("[Environment]::UserInteractive -and $session -ne 0 -and $explorer.Count -gt 0", workflow);
        Assert.Contains("FindWindow(\"Shell_TrayWnd\", null)", workflow);
        Assert.Contains("OpenInputDesktop(0, false, 0x0001)", workflow);
        Assert.Contains("available=$($available.ToString().ToLowerInvariant())", workflow);
        Assert.Contains("Write-Output \"::warning::$notice\"", workflow);
        Assert.Contains(">> $env:GITHUB_STEP_SUMMARY", workflow);
        Assert.Contains("- name: Smoke Windows native tray\n        if: runner.os == 'Windows' && steps.windows_tray_desktop.outputs.available == 'true'", workflow);
        Assert.Contains("-SkipPublish -CliPath $cli -SmokeSeconds 60", workflow);
        Assert.Contains("$cli = Join-Path $scratch 'cli/aspire.exe'", workflow);
        Assert.Contains("-PublishDirectory (Join-Path $scratch \"$rid/tray\")", workflow);
        Assert.Contains("eng/scripts/tray-registration-control/run.ps1 -Architecture ($rid -replace '^win-', '')", workflow);
        Assert.Contains("artifacts/bundle/aspire-ci-bundlepayload-$rid.tar.gz", workflow);
        var publish = File.ReadAllText(Path.Combine(RepoRoot.Path, "src/Aspire.Tray/Windows/publish.ps1"));
        Assert.Contains("$startInfo.ArgumentList.Add($argument)", publish);
        Assert.Contains("$process.StandardOutput.ReadToEndAsync()", publish);
        Assert.Contains("$process.StandardError.ReadToEndAsync()", publish);
        Assert.Contains("$process.WaitForExit(($SmokeSeconds + 30) * 1000)", publish);
        Assert.Contains("tools/CreateLayout/verify-windows-tray-payload.ps1", publish);
        Assert.Contains("-PublishDirectory $output -Rid \"win-$Architecture\"", publish);
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
        using var builder = new LayoutBuilder(layout, workspace.Path, rid, "test", false, null, source);
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

    private static XDocument LoadProject(string path) => XDocument.Load(Path.Combine(RepoRoot.Path, path));

    private static XElement Target(XDocument project, string name)
        => Assert.Single(project.Descendants("Target"), target => target.Attribute("Name")?.Value == name);

    private static string CreateApp(string workspace)
    {
        var source = Path.Combine(workspace, "source with spaces", "Aspire Tray.app");
        foreach (var file in new[]
        {
            "Contents/MacOS/aspire-tray",
            "Contents/Info.plist",
            "Contents/Resources/Aspire.icns",
            "Contents/Resources/.hidden",
            "Contents/_CodeSignature/CodeResources"
        })
        {
            var path = Path.Combine(source, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(source, "Contents/MacOS/aspire-tray"),
                UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return source;
    }
}
