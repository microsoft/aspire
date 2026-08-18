// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Projects;

public class DefaultLanguageDiscoveryTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task GetAvailableLanguagesAsync_ReturnsCSharpLanguage()
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());

        var languages = await discovery.GetAvailableLanguagesAsync().DefaultTimeout();

        var csharp = languages.FirstOrDefault(l => l.LanguageId.Value == KnownLanguageId.CSharp);
        Assert.NotNull(csharp);
        Assert.Equal(KnownLanguageId.CSharpDisplayName, csharp.DisplayName);
    }

    [Theory]
    [InlineData("*.csproj")]
    [InlineData("*.fsproj")]
    [InlineData("*.vbproj")]
    [InlineData("apphost.cs")]
    public async Task GetAvailableLanguagesAsync_CSharpLanguageHasExpectedDetectionPatterns(string expectedPattern)
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());

        var languages = await discovery.GetAvailableLanguagesAsync().DefaultTimeout();

        var csharp = languages.First(l => l.LanguageId.Value == KnownLanguageId.CSharp);
        Assert.Contains(expectedPattern, csharp.DetectionPatterns);
    }

    [Fact]
    public async Task GetAvailableLanguagesAsync_ReturnsTypeScriptLanguage()
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());

        var languages = await discovery.GetAvailableLanguagesAsync().DefaultTimeout();

        var typescript = languages.FirstOrDefault(l => l.LanguageId.Value == "typescript/nodejs");
        Assert.NotNull(typescript);
        Assert.Equal("TypeScript (Node.js)", typescript.DisplayName);
        Assert.Contains("apphost.mts", typescript.DetectionPatterns);
        Assert.Contains("apphost.ts", typescript.DetectionPatterns);
        Assert.Equal("apphost.mts", typescript.AppHostFileName);
    }

    [Fact]
    public async Task GetAvailableLanguagesAsync_ReturnsPythonLanguage()
    {
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.ExperimentalPolyglotPython, true);
        var discovery = new DefaultLanguageDiscovery(features);

        var languages = await discovery.GetAvailableLanguagesAsync().DefaultTimeout();

        var python = languages.FirstOrDefault(l => l.LanguageId.Value == KnownLanguageId.Python);
        Assert.NotNull(python);
        Assert.Equal(KnownLanguageId.PythonDisplayName, python.DisplayName);
        Assert.Contains("apphost.py", python.DetectionPatterns);
    }

    [Fact]
    public async Task GetAvailableLanguagesAsync_ExcludesExperimentalLanguagesByDefault()
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());

        var languages = (await discovery.GetAvailableLanguagesAsync().DefaultTimeout()).ToList();

        Assert.Null(languages.FirstOrDefault(l => l.LanguageId.Value == KnownLanguageId.Python));
        Assert.Null(languages.FirstOrDefault(l => l.LanguageId.Value == KnownLanguageId.Go));
        Assert.Null(languages.FirstOrDefault(l => l.LanguageId.Value == KnownLanguageId.Java));
        Assert.Null(languages.FirstOrDefault(l => l.LanguageId.Value == KnownLanguageId.Rust));
    }

    [Theory]
    [InlineData(KnownLanguageId.Python, "experimentalPolyglot:python")]
    [InlineData(KnownLanguageId.Go, "experimentalPolyglot:go")]
    [InlineData(KnownLanguageId.Java, "experimentalPolyglot:java")]
    [InlineData(KnownLanguageId.Rust, "experimentalPolyglot:rust")]
    public async Task GetAvailableLanguagesAsync_IncludesExperimentalLanguageWhenFlagEnabled(string languageId, string featureFlag)
    {
        var features = new TestFeatures();
        features.SetFeature(featureFlag, true);
        var discovery = new DefaultLanguageDiscovery(features);

        var languages = (await discovery.GetAvailableLanguagesAsync().DefaultTimeout()).ToList();

        Assert.NotNull(languages.FirstOrDefault(l => l.LanguageId.Value == languageId));
    }

    [Theory]
    [InlineData("test.csproj", KnownLanguageId.CSharp)]
    [InlineData("Test.csproj", KnownLanguageId.CSharp)]
    [InlineData("test.fsproj", KnownLanguageId.CSharp)]
    [InlineData("test.vbproj", KnownLanguageId.CSharp)]
    [InlineData("apphost.cs", KnownLanguageId.CSharp)]
    [InlineData("AppHost.cs", KnownLanguageId.CSharp)]
    [InlineData("APPHOST.CS", KnownLanguageId.CSharp)]
    [InlineData("apphost.mts", "typescript/nodejs")]
    [InlineData("AppHost.mts", "typescript/nodejs")]
    [InlineData("apphost.ts", "typescript/nodejs")]
    [InlineData("AppHost.ts", "typescript/nodejs")]
    public void GetLanguageByFile_ReturnsCorrectLanguage(string fileName, string expectedLanguageId)
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());
        var file = new FileInfo(Path.Combine(Path.GetTempPath(), fileName));

        var language = discovery.GetLanguageByFile(file);

        Assert.NotNull(language);
        Assert.Equal(expectedLanguageId, language.LanguageId.Value);
    }

    [Theory]
    [InlineData("test.txt")]
    [InlineData("program.cs")]
    [InlineData("random.js")]
    public void GetLanguageByFile_ReturnsNullForUnknownFiles(string fileName)
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());
        var file = new FileInfo(Path.Combine(Path.GetTempPath(), fileName));

        var language = discovery.GetLanguageByFile(file);

        Assert.Null(language);
    }

    [Fact]
    public void GetLanguageByFile_ReturnsNullForExperimentalLanguageWhenFlagDisabled()
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());
        var file = new FileInfo(Path.Combine(Path.GetTempPath(), "apphost.go"));

        var language = discovery.GetLanguageByFile(file);

        Assert.Null(language);
    }

    [Fact]
    public void GetLanguageByFile_ReturnsExperimentalLanguageWhenFlagEnabled()
    {
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.ExperimentalPolyglotGo, true);
        var discovery = new DefaultLanguageDiscovery(features);
        var file = new FileInfo(Path.Combine(Path.GetTempPath(), "apphost.go"));

        var language = discovery.GetLanguageByFile(file);

        Assert.NotNull(language);
        Assert.Equal(KnownLanguageId.Go, language.LanguageId.Value);
    }

    [Theory]
    [InlineData(KnownLanguageId.CSharp)]
    [InlineData("typescript/nodejs")]
    public void GetLanguageById_ReturnsCorrectLanguage(string languageId)
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());

        var language = discovery.GetLanguageById(new LanguageId(languageId));

        Assert.NotNull(language);
        Assert.Equal(languageId, language.LanguageId.Value);
    }

    /// <summary>
    /// Covers the two hops an AppHost makes on the way to a project handler: the recursive scan that
    /// locates the file, and the language resolution that runs on whatever the scan returned.
    /// </summary>
    /// <remarks>
    /// The two hops match differently, which is easy to misread as a gap. <see cref="LanguageInfo.FindInDirectory"/>
    /// passes each pattern to <c>Directory.EnumerateFiles</c>, which accepts a relative directory in the
    /// pattern, so <c>src/main/java/AppHost.java</c> locates the conventional Maven and Gradle layout.
    /// <c>GetLanguageByFile</c> then sees only <c>FileInfo.Name</c>, so that same pattern cannot match
    /// there and resolution happens through the bare <c>AppHost.java</c> pattern instead. Both patterns
    /// are therefore load-bearing, and dropping the bare one as redundant would leave a conventional
    /// AppHost that is found but has no language.
    /// </remarks>
    [Theory]
    [InlineData("AppHost.java")]
    [InlineData("src/main/java/AppHost.java")]
    public void JavaAppHostIsBothLocatedAndResolvedInEveryLayout(string relativePath)
    {
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.ExperimentalPolyglotJava, true);
        var discovery = new DefaultLanguageDiscovery(features);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var appHostPath = Path.Combine(workspace.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(appHostPath)!);
        File.WriteAllText(appHostPath, "void main(String[] args) { }");

        var java = discovery.GetLanguageById(new LanguageId(KnownLanguageId.Java));
        Assert.NotNull(java);

        var located = java.FindInDirectory(workspace.Path);
        Assert.Equal(appHostPath, located);

        var language = discovery.GetLanguageByFile(new FileInfo(located!));
        Assert.NotNull(language);
        Assert.Equal(KnownLanguageId.Java, language.LanguageId.Value);
    }
    [Fact]
    public void GetLanguageById_ReturnsNullForUnknownLanguage()
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());

        var language = discovery.GetLanguageById(new LanguageId("unknown"));

        Assert.Null(language);
    }

    [Fact]
    public void GetLanguageById_ReturnsNullForExperimentalLanguageWhenFlagDisabled()
    {
        var discovery = new DefaultLanguageDiscovery(new TestFeatures());

        var language = discovery.GetLanguageById(new LanguageId(KnownLanguageId.Rust));

        Assert.Null(language);
    }

    [Fact]
    public void GetLanguageById_ReturnsExperimentalLanguageWhenFlagEnabled()
    {
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.ExperimentalPolyglotRust, true);
        var discovery = new DefaultLanguageDiscovery(features);

        var language = discovery.GetLanguageById(new LanguageId(KnownLanguageId.Rust));

        Assert.NotNull(language);
        Assert.Equal(KnownLanguageId.Rust, language.LanguageId.Value);
    }
}
