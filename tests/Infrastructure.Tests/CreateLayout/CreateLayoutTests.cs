// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tools.CreateLayout;
using Xunit;

namespace Infrastructure.Tests.CreateLayout;

public class CreateLayoutTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("win-x64", ".exe")]
    [InlineData("linux-x64", "")]
    [InlineData("osx-arm64", "")]
    public void CopyAppHostServer_PreservesPayloadAndProductNameWithoutSymbols(string rid, string extension)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var source = Path.Combine(workspace.Path, "bin", "Aspire.Hosting.Native.Server", "Debug", "net11.0", rid, "publish");
        var symbolDirectory = Path.Combine(source, "Aspire.Hosting.Native.Server.dSYM");
        Directory.CreateDirectory(symbolDirectory);
        File.WriteAllText(Path.Combine(source, "Aspire.Hosting.Native.Server" + extension), "server");
        File.WriteAllText(Path.Combine(source, "ats-contract.json"), "contract");
        File.WriteAllText(Path.Combine(source, "sidecar.json"), "sidecar");
        File.WriteAllText(Path.Combine(source, "Aspire.Hosting.Native.Server.pdb"), "symbols");
        File.WriteAllText(Path.Combine(symbolDirectory, "symbols"), "symbols");
        var output = Path.Combine(workspace.Path, "layout");
        using var builder = new LayoutBuilder(output, workspace.Path, rid, "Debug", "1.0.0", verbose: false,
            trayAppPath: null, windowsTrayPath: null);

        builder.CopyAppHostServer();

        var destination = Path.Combine(output, "apphost-server");
        Assert.Equal(["aspire-apphost-server" + extension, "ats-contract.json", "sidecar.json"],
            Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("server", File.ReadAllText(Path.Combine(destination, "aspire-apphost-server" + extension)));
        Assert.Equal("contract", File.ReadAllText(Path.Combine(destination, "ats-contract.json")));
        Assert.Equal("sidecar", File.ReadAllText(Path.Combine(destination, "sidecar.json")));
    }

    [Theory]
    [InlineData("publish", null)]
    [InlineData("publish", "")]
    [InlineData("native", null)]
    [InlineData("native", "")]
    public void CopyAppHostServer_RejectsMissingOrEmptyContract(string directory, string? contract)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var source = Path.Combine(workspace.Path, "bin", "Aspire.Hosting.Native.Server", "Debug", "net11.0", "linux-x64", directory);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Aspire.Hosting.Native.Server"), "server");
        if (contract is not null)
        {
            File.WriteAllText(Path.Combine(source, "ats-contract.json"), contract);
        }
        var output = Path.Combine(workspace.Path, "layout");
        using var builder = new LayoutBuilder(output, workspace.Path, "linux-x64", "Debug", "1.0.0", false, null, null);

        var failure = Assert.Throws<InvalidOperationException>(builder.CopyAppHostServer);

        Assert.Contains("ats-contract.json", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(output, "apphost-server")));
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("linux-musl-x64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public void CopyTerminalHostRequiresNativeDependenciesAndExcludesSymbols(string rid)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var publish = Path.Combine(workspace.Path, "bin", "Aspire.TerminalHost", "Debug", "net11.0", rid, "publish");
        var files = Aspire.Shared.TerminalHostPayload.GetRequiredFiles(rid);
        foreach (var file in files)
        {
            var path = Path.Combine(publish, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        File.WriteAllText(Path.Combine(publish, "Aspire.TerminalHost.pdb"), "symbols");
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, rid, "Debug", "1.0", false, null, null);
        builder.CopyTerminalHost();
        var target = Path.Combine(workspace.Path, "layout", "terminalhost");
        Assert.Equal(files.Order(StringComparer.Ordinal), Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(target, path).Replace('\\', '/')).Order(StringComparer.Ordinal));

        File.Delete(Path.Combine(publish, files[^1]));
        Assert.Throws<InvalidOperationException>(builder.CopyTerminalHost);
    }

    [Fact]
    public void TerminalHostPayloadValidationUsesCurrentRidAndRequiresNonEmptyFiles()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var files = Aspire.Shared.TerminalHostPayload.GetRequiredFiles(rid);

        Assert.False(Aspire.Shared.TerminalHostPayload.IsValid(workspace.Path));
        foreach (var file in files)
        {
            var path = Path.Combine(workspace.Path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }

        Assert.True(Aspire.Shared.TerminalHostPayload.IsValid(workspace.Path));
        Assert.True(Aspire.Shared.TerminalHostPayload.IsValid(workspace.Path, rid));

        File.WriteAllText(Path.Combine(workspace.Path, files[^1]), string.Empty);

        Assert.False(Aspire.Shared.TerminalHostPayload.IsValid(workspace.Path));
        Assert.False(Aspire.Shared.TerminalHostPayload.IsValid(workspace.Path, rid));
    }

    [Theory]
    [InlineData("Debug", "Release")]
    [InlineData("Release", "Debug")]
    [InlineData("Custom", "Release")]
    public void FindPublishPath_UsesOnlyRequestedConfiguration(string configuration, string otherConfiguration)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, "win-x64", configuration, "1.0.0", verbose: false, trayAppPath: null, windowsTrayPath: null);
        var expectedPaths = GetPublishPaths(workspace.Path, configuration);

        foreach (var path in expectedPaths.Concat(GetPublishPaths(workspace.Path, otherConfiguration)))
        {
            Directory.CreateDirectory(path);
        }

        foreach (var expectedPath in expectedPaths)
        {
            Assert.Equal(expectedPath, builder.FindPublishPath("Aspire.Dashboard", "net11.0"));
            Directory.Delete(expectedPath);
        }

        Assert.Null(builder.FindPublishPath("Aspire.Dashboard", "net11.0"));
    }

    [Fact]
    public void FindPublishPath_RequireRidSpecific_IgnoresNonRidPublish()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, "win-x64", "Debug", "1.0.0", verbose: false, trayAppPath: null, windowsTrayPath: null);
        var nonRidPath = GetPublishPaths(workspace.Path, "Debug")[^1];
        Directory.CreateDirectory(nonRidPath);

        Assert.Null(builder.FindPublishPath("Aspire.Dashboard", "net11.0", requireRidSpecific: true));
        Assert.Equal(nonRidPath, builder.FindPublishPath("Aspire.Dashboard", "net11.0"));
    }

    [Fact]
    public async Task Main_RequiresConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var outputPath = Path.Combine(workspace.Path, "layout");

        var exitCode = await Aspire.Tools.CreateLayout.Program.Main(
        [
            "--output", outputPath,
            "--artifacts", workspace.Path,
            "--rid", "win-x64"
        ]);

        Assert.Equal(1, exitCode);
        Assert.False(Directory.Exists(outputPath));
    }

    [Fact]
    public void CopyDashboard_MissingExplicitDirectoryDoesNotFallBackToDiscoverablePayload()
    {
        const string rid = "linux-x64";
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var discoveredPath = CreateDashboardPublishOutput(workspace.Path, rid, "libe_sqlite3.so");
        var explicitPath = Path.Combine(workspace.Path, "missing prepared Dashboard");
        var outputPath = Path.Combine(workspace.Path, "layout");
        using var builder = new LayoutBuilder(outputPath, workspace.Path, rid, "Debug", "1.0.0",
            verbose: false, trayAppPath: null, windowsTrayPath: null, dashboardPath: explicitPath);

        var exception = Assert.Throws<InvalidOperationException>(builder.CopyDashboard);

        Assert.Equal($"Dashboard publish directory not found at {explicitPath}", exception.Message);
        Assert.Equal(discoveredPath, builder.FindPublishPath("Aspire.Dashboard", "net11.0", requireRidSpecific: true));
        Assert.False(Directory.Exists(Path.Combine(outputPath, "dashboard")));
    }

    [Theory]
    [InlineData("win-x64", "e_sqlite3.dll")]
    [InlineData("win-arm64", "e_sqlite3.dll")]
    [InlineData("linux-x64", "libe_sqlite3.so")]
    [InlineData("linux-arm64", "libe_sqlite3.so")]
    [InlineData("linux-musl-x64", "libe_sqlite3.so")]
    [InlineData("osx-x64", "libe_sqlite3.dylib")]
    [InlineData("osx-arm64", "libe_sqlite3.dylib")]
    public void CopyDashboard_CopiesCompletePublishPayloadWithoutSymbols(string rid, string sqliteLibraryName)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var publishPath = CreateDashboardPublishOutput(workspace.Path, rid, sqliteLibraryName);
        var executableName = rid.StartsWith("win-", StringComparison.Ordinal) ? "Aspire.Dashboard.exe" : "Aspire.Dashboard";
        string[] expectedFiles =
        [
            executableName,
            sqliteLibraryName,
            "appsettings.json",
            "other-native-library.dll",
            "locales/en/messages.json",
            "wwwroot/_framework/blazor.web.js"
        ];
        string[] symbolFiles =
        [
            "Aspire.Dashboard.pdb",
            "Aspire.Dashboard.dbg",
            "Aspire.Dashboard.dSYM/Contents/Resources/DWARF/Aspire.Dashboard",
            "locales/nested.pdb",
            "locales/nested.dbg",
            "locales/nested.dSYM/Contents/Resources/DWARF/nested"
        ];
        foreach (var relativePath in expectedFiles.Concat(symbolFiles))
        {
            var filePath = Path.Combine(publishPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, relativePath);
        }

        var outputPath = Path.Combine(workspace.Path, "layout");
        using var builder = new LayoutBuilder(outputPath, workspace.Path, rid, "Debug", "1.0.0", verbose: false, trayAppPath: null, windowsTrayPath: null);

        builder.CopyDashboard();

        var dashboardPath = Path.Combine(outputPath, "dashboard");
        var actualFiles = Directory.GetFiles(dashboardPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(dashboardPath, path).Replace(Path.DirectorySeparatorChar, '/'));
        Assert.Equal(expectedFiles.Order(StringComparer.Ordinal), actualFiles.Order(StringComparer.Ordinal));
        foreach (var relativePath in expectedFiles)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(publishPath, relativePath)), File.ReadAllBytes(Path.Combine(dashboardPath, relativePath)));
        }
    }

    [Fact]
    public void CopyDashboard_MissingStaticAssets_Throws()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var publishPath = CreateDashboardPublishOutput(workspace.Path, "win-x64", "e_sqlite3.dll");
        var wwwrootPath = Path.Combine(publishPath, "wwwroot");
        Directory.Delete(wwwrootPath, recursive: true);
        var outputPath = Path.Combine(workspace.Path, "layout");
        using var builder = new LayoutBuilder(outputPath, workspace.Path, "win-x64", "Debug", "1.0.0", verbose: false, trayAppPath: null, windowsTrayPath: null);

        var exception = Assert.Throws<InvalidOperationException>(builder.CopyDashboard);

        Assert.Equal($"Native AOT Dashboard static assets not found at {wwwrootPath}", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(outputPath, "dashboard")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyDashboard_MissingOrEmptyBlazorScript_Throws(bool empty)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var publishPath = CreateDashboardPublishOutput(workspace.Path, "win-x64", "e_sqlite3.dll");
        var blazorScriptPath = Path.Combine(publishPath, "wwwroot", "_framework", "blazor.web.js");
        if (empty)
        {
            File.WriteAllBytes(blazorScriptPath, []);
        }
        else
        {
            File.Delete(blazorScriptPath);
        }

        var outputPath = Path.Combine(workspace.Path, "layout");
        using var builder = new LayoutBuilder(outputPath, workspace.Path, "win-x64", "Debug", "1.0.0", verbose: false, trayAppPath: null, windowsTrayPath: null);

        var exception = Assert.Throws<InvalidOperationException>(builder.CopyDashboard);

        Assert.Equal($"Native AOT Dashboard Blazor script missing or empty at {blazorScriptPath}", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(outputPath, "dashboard")));
    }

    [Theory]
    [InlineData("win-x64", "e_sqlite3.dll", false)]
    [InlineData("win-x64", "e_sqlite3.dll", true)]
    [InlineData("linux-x64", "libe_sqlite3.so", false)]
    [InlineData("linux-x64", "libe_sqlite3.so", true)]
    [InlineData("osx-arm64", "libe_sqlite3.dylib", false)]
    [InlineData("osx-arm64", "libe_sqlite3.dylib", true)]
    public void CopyDashboard_MissingOrEmptySqliteLibrary_Throws(string rid, string sqliteLibraryName, bool empty)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var publishPath = CreateDashboardPublishOutput(workspace.Path, rid, sqliteLibraryName);
        var sqliteLibraryPath = Path.Combine(publishPath, sqliteLibraryName);
        if (empty)
        {
            File.WriteAllBytes(sqliteLibraryPath, []);
        }
        else
        {
            File.Delete(sqliteLibraryPath);
        }

        var outputPath = Path.Combine(workspace.Path, "layout");
        using var builder = new LayoutBuilder(outputPath, workspace.Path, rid, "Debug", "1.0.0", verbose: false, trayAppPath: null, windowsTrayPath: null);

        var exception = Assert.Throws<InvalidOperationException>(builder.CopyDashboard);

        Assert.Equal($"Native AOT Dashboard SQLite library missing or empty at {sqliteLibraryPath}", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(outputPath, "dashboard")));
    }

    [Fact]
    public void CopyDashboard_ExplicitDirectoryPreservesPayloadFiltering()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var publish = CreateDashboardPublishOutput(workspace.Path, "win-x64", "e_sqlite3.dll");
        var explicitPath = Path.Combine(workspace.Path, "prepared dashboard outside artifacts");
        Directory.Move(publish, explicitPath);
        File.WriteAllText(Path.Combine(explicitPath, "Aspire.Dashboard.exe"), "explicit executable");
        File.WriteAllText(Path.Combine(explicitPath, "Aspire.Dashboard.pdb"), "symbols");
        Directory.CreateDirectory(Path.Combine(explicitPath, "nested.dSYM"));
        File.WriteAllText(Path.Combine(explicitPath, "nested.dSYM", "symbols"), "symbols");
        File.WriteAllText(Path.Combine(explicitPath, "Aspire.Dashboard.dbg"), "symbols");
        var output = Path.Combine(workspace.Path, "layout");
        using var builder = new LayoutBuilder(output, workspace.Path, "win-x64", "Debug", "1.0", false, null, null, explicitPath);

        builder.CopyDashboard();

        var dashboard = Path.Combine(output, "dashboard");
        Assert.Equal(["Aspire.Dashboard.exe", "e_sqlite3.dll", "wwwroot/_framework/blazor.web.js"],
            Directory.GetFiles(dashboard, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(dashboard, path).Replace('\\', '/')).Order(StringComparer.Ordinal));
        Assert.Equal("explicit executable", File.ReadAllText(Path.Combine(dashboard, "Aspire.Dashboard.exe")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void CopyDashboard_EmptyExplicitDirectoryIsRejected(string path)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        CreateDashboardPublishOutput(workspace.Path, "win-x64", "e_sqlite3.dll");
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path,
            "win-x64", "Debug", "1.0", false, null, null, path);

        var exception = Assert.Throws<InvalidOperationException>(builder.CopyDashboard);

        Assert.Equal("--dashboard requires a nonempty publish directory.", exception.Message);
    }

    [Fact]
    public void CopyDashboard_DiscoveryFailureIdentifiesSearchLocations()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path,
            "win-x64", "Debug", "1.0", false, null, null);

        var exception = Assert.Throws<InvalidOperationException>(builder.CopyDashboard);

        Assert.Contains("win-x64", exception.Message);
        Assert.Contains("Debug", exception.Message);
        foreach (var path in GetPublishPaths(workspace.Path, "Debug")[..^1])
        {
            Assert.Contains(path, exception.Message);
        }
    }

    private static string CreateDashboardPublishOutput(string artifactsPath, string rid, string sqliteLibraryName)
    {
        var publishPath = Path.Combine(artifactsPath, "bin", "Aspire.Dashboard", rid, "Debug", "net11.0", rid, "publish");
        var frameworkPath = Path.Combine(publishPath, "wwwroot", "_framework");
        Directory.CreateDirectory(frameworkPath);
        var executableName = rid.StartsWith("win-", StringComparison.Ordinal) ? "Aspire.Dashboard.exe" : "Aspire.Dashboard";
        File.WriteAllText(Path.Combine(publishPath, executableName), "executable");
        File.WriteAllText(Path.Combine(publishPath, sqliteLibraryName), "sqlite");
        File.WriteAllText(Path.Combine(frameworkPath, "blazor.web.js"), "static asset");

        return publishPath;
    }

    private static string[] GetPublishPaths(string artifactsPath, string configuration) =>
    [
        Path.Combine(artifactsPath, "bin", "Aspire.Dashboard", "win-x64", configuration, "net11.0", "win-x64", "publish"),
        Path.Combine(artifactsPath, "bin", "Aspire.Dashboard", configuration, "net11.0", "win-x64", "publish"),
        Path.Combine(artifactsPath, "bin", "Aspire.Dashboard", configuration, "net11.0", "win-x64", "native"),
        Path.Combine(artifactsPath, "bin", "Aspire.Dashboard", configuration, "net11.0", "publish")
    ];
}