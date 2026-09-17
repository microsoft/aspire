// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Templating;

public class JavaAppHostTemplateTests
{
    /// <summary>
    /// The Java runtime spec launches the AppHost with <c>java -cp .java-build AppHost</c>, which names
    /// the class in the default package. A <c>package</c> declaration compiles the class into a
    /// subdirectory instead, so the launch fails with <c>ClassNotFoundException: AppHost</c> even though
    /// compilation succeeded.
    /// </summary>
    [Fact]
    public void JavaStarterAppHost_IsInTheDefaultPackageSoTheRunnerCanLoadIt()
    {
        var lines = File.ReadAllLines(GetJavaStarterAppHostPath());

        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith("package ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Without a package declaration the AppHost no longer shares a package with the generated SDK, so
    /// it has to import it explicitly or every generated type fails to resolve.
    /// </summary>
    [Fact]
    public void JavaStarterAppHost_ImportsTheGeneratedSdkPackage()
    {
        var lines = File.ReadAllLines(GetJavaStarterAppHostPath());

        Assert.Contains("import aspire.*;", lines.Select(line => line.Trim()));
    }

    /// <summary>
    /// The generated SDK declares <c>package aspire;</c>, so an editor can only resolve it when
    /// <c>.aspire/modules</c> is a source root. javac does not need this because the CLI names every
    /// generated file explicitly in its argument file, but the Java language server builds from the
    /// project model instead and reports "package aspire does not exist" against an AppHost that runs
    /// perfectly well. The template recommends the Java extension pack, so the scaffolded project has
    /// to arrive with the source root already registered.
    /// </summary>
    [Fact]
    public void JavaStarterTemplate_RegistersTheGeneratedSdkAsASourceRoot()
    {
        var path = Path.Combine(GetJavaStarterDirectory(), ".vscode", "settings.json");
        Assert.True(File.Exists(path), $"Expected the java-starter VS Code settings at {path}");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var sourcePaths = document.RootElement
            .GetProperty("java.project.sourcePaths")
            .EnumerateArray()
            .Select(element => element.GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal([".", ".aspire/modules"], sourcePaths);
    }

    /// <summary>
    /// The three tests above each assert one property the runner depends on, so between them a
    /// template that had lost its entire class body still passed: nothing checked that the file
    /// declares an AppHost at all, or that the sample it scaffolds still builds a real application.
    /// Snapshotting the whole file closes that gap and makes any edit to the first thing a Java user
    /// ever sees a deliberate, reviewable change.
    /// </summary>
    [Fact]
    public async Task JavaStarterAppHost_MatchesTheScaffoldedSource()
    {
        await Verify(File.ReadAllText(GetJavaStarterAppHostPath()), extension: "java");
    }

    /// <summary>
    /// The Java starter began life as a copy of the TypeScript starter, so it scaffolded an Express
    /// API and a React frontend and no Java at all beyond the AppHost. Every other same-language
    /// starter ships a service written in its own language — go-starter has api/main.go — and a Java
    /// user picking the Java template has no reason to expect otherwise. This asserts the template
    /// actually contains a buildable Java service.
    /// </summary>
    [Fact]
    public void JavaStarterTemplate_ScaffoldsAJavaService()
    {
        var apiDirectory = Path.Combine(GetJavaStarterDirectory(), "api");

        var buildPath = Path.Combine(apiDirectory, "build.gradle");
        Assert.True(File.Exists(buildPath), $"Expected the java-starter API's Gradle build at {buildPath}");

        var build = File.ReadAllText(buildPath);
        Assert.Contains("id 'org.springframework.boot' version '3.5.14'", build, StringComparison.Ordinal);
        Assert.Contains("languageVersion = JavaLanguageVersion.of(25)", build, StringComparison.Ordinal);

        var settingsPath = Path.Combine(apiDirectory, "settings.gradle");
        Assert.True(File.Exists(settingsPath), $"Expected the java-starter API's Gradle settings at {settingsPath}");

        var javaSources = Directory
            .EnumerateFiles(apiDirectory, "*.java", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ApiApplication.java", "WeatherForecastController.java"], javaSources);
    }

    /// <summary>
    /// The generated Java SDK surface is built from the <c>packages</c> in aspire.config.json. While
    /// the starter listed only Aspire.Hosting.JavaScript, a freshly scaffolded Java project had no
    /// <c>addSpringBootApp</c>, <c>addJavaApp</c>, or <c>addQuarkusApp</c> to call, so the AppHost
    /// could not add a Java resource until the user hand-edited the config.
    /// </summary>
    [Fact]
    public void JavaStarterTemplate_DeclaresTheJavaHostingPackage()
    {
        var path = Path.Combine(GetJavaStarterDirectory(), "aspire.config.json");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var packages = document.RootElement
            .GetProperty("packages")
            .EnumerateObject()
            .Select(package => package.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Aspire.Hosting.Java", "Aspire.Hosting.JavaScript"], packages);
    }

    /// <summary>
    /// The AppHost never names a build tool: addSpringBootApp resolves one from the project, and the
    /// resolver prefers a wrapper over whatever <c>gradle</c> happens to be on PATH. Pinning the
    /// distribution and its checksum keeps every scaffold on the same verified Gradle version.
    /// </summary>
    [Fact]
    public void JavaStarterTemplate_ShipsAPinnedGradleWrapper()
    {
        var apiDirectory = Path.Combine(GetJavaStarterDirectory(), "api");

        foreach (var relativePath in new[] { "gradlew", "gradlew.bat", "gradle/wrapper/gradle-wrapper.jar" })
        {
            var path = Path.Combine(apiDirectory, relativePath);
            Assert.True(File.Exists(path), $"Expected the java-starter Gradle wrapper at {path}");
        }

        var propertiesPath = Path.Combine(apiDirectory, "gradle", "wrapper", "gradle-wrapper.properties");
        Assert.True(File.Exists(propertiesPath), $"Expected the java-starter wrapper properties at {propertiesPath}");

        var properties = File.ReadAllText(propertiesPath);
        Assert.Contains("gradle-9.7.0-bin.zip", properties, StringComparison.Ordinal);
        Assert.Contains("distributionSha256Sum=84fbba45c7f4c64abc77460e1c00f541e9f960e3c7ed2538f1ede19eacd873ae", properties, StringComparison.Ordinal);

        foreach (var relativePath in new[] { "pom.xml", "mvnw", "mvnw.cmd", ".mvn" })
        {
            var path = Path.Combine(apiDirectory, relativePath);
            Assert.False(File.Exists(path) || Directory.Exists(path), $"Did not expect a Maven build or wrapper path at {path}");
        }
    }

    /// <summary>
    /// The Vite dev server proxies /api to the endpoint variable Aspire injects for
    /// <c>frontend.withReference(api)</c>, which is named after the referenced resource and its
    /// endpoint — <c>API_HTTP</c> for a resource called <c>api</c> with the default <c>http</c>
    /// endpoint. Renaming the resource in AppHost.java silently breaks the proxy because
    /// vite.config.ts falls back to an undefined target, so the two are pinned together here.
    /// </summary>
    [Fact]
    public void JavaStarterTemplate_FrontendProxiesToTheApiResourceEndpointVariable()
    {
        var appHost = File.ReadAllText(GetJavaStarterAppHostPath());
        Assert.Contains("addSpringBootApp(\"api\", \"./api\")", appHost, StringComparison.Ordinal);

        var viteConfig = File.ReadAllText(Path.Combine(GetJavaStarterDirectory(), "frontend", "vite.config.ts"));
        Assert.Contains("process.env.API_HTTPS || process.env.API_HTTP", viteConfig, StringComparison.Ordinal);
    }

    private static string GetJavaStarterAppHostPath()
    {
        var path = Path.Combine(GetJavaStarterDirectory(), "AppHost.java");
        Assert.True(File.Exists(path), $"Expected the java-starter AppHost at {path}");

        return path;
    }

    private static string GetJavaStarterDirectory()
        => Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates", "java-starter");

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
