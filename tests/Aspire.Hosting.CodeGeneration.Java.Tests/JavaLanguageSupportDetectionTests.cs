// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.Java.Tests;

/// <summary>
/// Covers the layouts a Java AppHost is allowed to take on disk.
/// </summary>
/// <remarks>
/// Java is the only language whose detection patterns include a path rather than a bare file name,
/// because a Maven or Gradle AppHost lives at the build tool's source root instead of the project root.
/// The pattern list is therefore load-bearing in two different ways, and these assert the one that is
/// path-aware: <see cref="JavaLanguageSupport.Detect"/> resolves each pattern against the directory.
/// The CLI's own matching sees only a file name, which the bare <c>AppHost.java</c> pattern covers, and
/// is asserted by <c>DefaultLanguageDiscoveryTests.JavaAppHostIsBothLocatedAndResolvedInEveryLayout</c>.
/// </remarks>
public class JavaLanguageSupportDetectionTests
{
    [Theory]
    [InlineData("AppHost.java")]
    [InlineData("src/main/java/AppHost.java")]
    public void DetectFindsTheAppHostInEverySupportedLayout(string relativePath)
    {
        using var workspace = new TempDirectory();
        var expected = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var appHostPath = Path.Combine(workspace.Path, expected);
        Directory.CreateDirectory(Path.GetDirectoryName(appHostPath)!);
        File.WriteAllText(appHostPath, "void main(String[] args) { }");

        var result = new JavaLanguageSupport().Detect(workspace.Path);

        Assert.True(result.IsValid);
        Assert.Equal("java", result.Language);
        // Reported relative to the directory that was scanned, and separated for the current platform,
        // because the caller combines it back onto that directory to open the file.
        Assert.Equal(expected, result.AppHostFile);
    }

    /// <summary>
    /// The flat layout is listed first, so it wins when a project somehow has both.
    /// </summary>
    [Fact]
    public void DetectPrefersTheFlatLayoutWhenBothArePresent()
    {
        using var workspace = new TempDirectory();
        File.WriteAllText(Path.Combine(workspace.Path, "AppHost.java"), "void main(String[] args) { }");
        var nested = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "main", "java"));
        File.WriteAllText(Path.Combine(nested.FullName, "AppHost.java"), "void main(String[] args) { }");

        Assert.Equal("AppHost.java", new JavaLanguageSupport().Detect(workspace.Path).AppHostFile);
    }

    [Fact]
    public void DetectReportsNotFoundForADirectoryWithNoAppHost()
    {
        using var workspace = new TempDirectory();
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        var result = new JavaLanguageSupport().Detect(workspace.Path);

        Assert.False(result.IsValid);
        Assert.Null(result.AppHostFile);
    }

    /// <summary>
    /// A Java source file that is not the AppHost must not be mistaken for one, including at the source
    /// root where the conventional AppHost would otherwise sit.
    /// </summary>
    [Theory]
    [InlineData("Application.java")]
    [InlineData("src/main/java/Application.java")]
    public void DetectIgnoresOtherJavaSources(string relativePath)
    {
        using var workspace = new TempDirectory();
        var path = Path.Combine(workspace.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "class Application { }");

        Assert.False(new JavaLanguageSupport().Detect(workspace.Path).IsValid);
    }

    private sealed class TempDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-java-detect");

        public string Path => _directory.FullName;

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
