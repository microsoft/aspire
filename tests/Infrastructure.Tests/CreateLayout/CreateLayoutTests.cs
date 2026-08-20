// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Tools.CreateLayout;
using Xunit;

namespace Infrastructure.Tests.CreateLayout;

public class CreateLayoutTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("Debug", "Release")]
    [InlineData("Release", "Debug")]
    [InlineData("Custom", "Release")]
    public void FindPublishPath_UsesOnlyRequestedConfiguration(string configuration, string otherConfiguration)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, "win-x64", configuration, "1.0.0", verbose: false);
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
        using var builder = new LayoutBuilder(Path.Combine(workspace.Path, "layout"), workspace.Path, "win-x64", "Debug", "1.0.0", verbose: false);
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
        using var builder = new LayoutBuilder(outputPath, workspace.Path, rid, "Debug", "1.0.0", verbose: false);

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
        using var builder = new LayoutBuilder(outputPath, workspace.Path, "win-x64", "Debug", "1.0.0", verbose: false);

        var exception = Assert.Throws<InvalidOperationException>(builder.CopyDashboard);

        Assert.Equal($"Native AOT Dashboard static assets not found at {wwwrootPath}", exception.Message);
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
        using var builder = new LayoutBuilder(outputPath, workspace.Path, rid, "Debug", "1.0.0", verbose: false);

        var exception = Assert.Throws<InvalidOperationException>(builder.CopyDashboard);

        Assert.Equal($"Native AOT Dashboard SQLite library missing or empty at {sqliteLibraryPath}", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(outputPath, "dashboard")));
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