// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.TypeSystem;

namespace Aspire.Cli.Tests.Projects;

public class JavaAppHostToolchainResolverTests(ITestOutputHelper outputHelper)
{
    private static string WriteWrapper(string directory, string wrapperName)
    {
        var path = Path.Combine(directory, wrapperName);
        File.WriteAllText(path, "");

        return path;
    }

    /// <summary>
    /// Asserts the wrapper invocation, accounting for Windows running the batch wrapper through the
    /// command interpreter rather than launching it directly.
    /// </summary>
    private static void AssertWrapperInvocation(string wrapperPath, string appHostDirectory, string[] toolArgs, CommandSpec actual)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", actual.Command);
            // "call" precedes the wrapper so the first token on the command line is never a quote,
            // which is what stops cmd.exe from stripping quotes around a path containing a space.
            Assert.Equal(
                ["/c", "call", Path.GetRelativePath(appHostDirectory, wrapperPath), .. toolArgs],
                actual.Args);

            return;
        }

        Assert.Equal("sh", actual.Command);
        Assert.Equal([wrapperPath, .. toolArgs], actual.Args);
    }

    private static RuntimeSpec CreateJavacRuntimeSpec()
    {
        return new RuntimeSpec
        {
            Language = "java",
            DisplayName = "Java",
            CodeGenLanguage = "Java",
            DetectionPatterns = ["AppHost.java", "src/main/java/AppHost.java"],
            InstallDependencies = null,
            ExtensionLaunchCapability = "java",
            // Mirrors JavaLanguageSupport.GetRuntimeSpec: the build-tool compile command is derived
            // from this one, so it has to have the same shape for the derivation to be meaningful.
            PreExecute =
            [
                new CommandSpec
                {
                    Command = "javac",
                    Args = ["--release", "25", "-d", ".java-build", "@.aspire/modules/sources.txt", "{appHostFile}"]
                }
            ],
            Execute = new CommandSpec
            {
                Command = "java",
                Args = ["-cp", ".java-build", "AppHost"]
            }
        };
    }

    [Fact]
    public void Resolve_WithNoBuildFile_UsesJavacSoAJdkIsTheOnlyRequirement()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        Assert.Equal(JavaAppHostToolchain.Javac, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot).Toolchain);
    }

    [Fact]
    public void Resolve_WithPomXml_UsesMaven()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        Assert.Equal(JavaAppHostToolchain.Maven, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot).Toolchain);
    }

    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    public void Resolve_WithGradleBuildFile_UsesGradle(string buildFileName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, buildFileName), "");

        Assert.Equal(JavaAppHostToolchain.Gradle, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot).Toolchain);
    }

    [Fact]
    public void Resolve_WithBothBuildFiles_PrefersMaven()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");

        Assert.Equal(JavaAppHostToolchain.Maven, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot).Toolchain);
    }

    [Fact]
    public void Resolve_IgnoresABuildFileInTheParentDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        var appHostDirectory = workspace.CreateDirectory("apphost");

        // A pom.xml above the AppHost usually belongs to an unrelated project that merely contains the
        // AppHost folder, so inheriting it would build the wrong thing.
        Assert.Equal(JavaAppHostToolchain.Javac, JavaAppHostToolchainResolver.Resolve(appHostDirectory).Toolchain);
    }

    [Fact]
    public void ApplyToRuntimeSpec_ForJavac_LeavesTheSpecUntouched()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var baseSpec = CreateJavacRuntimeSpec();

        // Existing single-file AppHosts must keep working byte for byte; adopting a build tool is opt-in.
        Assert.Same(baseSpec, JavaAppHostToolchainResolver.ApplyToRuntimeSpec(baseSpec, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot));
    }

    [Fact]
    public void ApplyToRuntimeSpec_ForMaven_RestoresCompilesThenLaunchesAPlainJvm()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        var wrapper = WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot);

        Assert.Equal("Java (Maven)", spec.DisplayName);

        AssertWrapperInvocation(
            wrapper,
            workspace.Path,
            ["-B", "-q", "dependency:copy-dependencies", $"-DoutputDirectory={Path.Combine("target", "aspire-deps")}", "-DincludeScope=runtime"],
            spec.InstallDependencies!);

        var compile = Assert.Single(spec.PreExecute!);
        // Compilation stays with javac even under Maven: the build tool cannot be told about the
        // generated SDK under .aspire/modules from the command line. The javac options and source
        // arguments are inherited from the base spec so the two toolchains cannot drift.
        Assert.Equal("javac", compile.Command);
        Assert.Equal(
            [
                "--release", "25",
                "-classpath", Path.Combine("target", "aspire-deps", "*"),
                "-sourcepath", $".{Path.PathSeparator}{Path.Combine("src", "main", "java")}",
                "-d", Path.Combine("target", "classes"),
                "@.aspire/modules/sources.txt",
                "{appHostFile}"
            ],
            compile.Args);

        // The AppHost is launched directly rather than through mvn exec:java so console signals reach
        // it, and without a {args} placeholder so the CLI appends real argv entries.
        Assert.Equal("java", spec.Execute.Command);
        Assert.Equal(
            ["-cp", $"{Path.Combine("target", "classes")}{Path.PathSeparator}{Path.Combine("target", "aspire-deps", "*")}", "AppHost"],
            spec.Execute.Args);
        Assert.DoesNotContain(spec.Execute.Args, arg => arg.Contains("{args}", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pom.xml", "target", new[] { "pom.xml" })]
    [InlineData("build.gradle", "build", new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradle/libs.versions.toml" })]
    public void ApplyToRuntimeSpec_DeclaresTheBuildDescriptorsAndStagedDependenciesAsCompileInputs(
        string buildFileName,
        string outputDirectoryName,
        string[] expectedDescriptors)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, buildFileName), "");
        WriteWrapper(workspace.Path, buildFileName == "pom.xml"
            ? OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw"
            : OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");

        var baseSpec = CreateJavacRuntimeSpec();
        var baseCompile = baseSpec.PreExecute![0];
        var specWithCheck = new RuntimeSpec
        {
            Language = baseSpec.Language,
            DisplayName = baseSpec.DisplayName,
            CodeGenLanguage = baseSpec.CodeGenLanguage,
            DetectionPatterns = baseSpec.DetectionPatterns,
            InstallDependencies = baseSpec.InstallDependencies,
            ExtensionLaunchCapability = baseSpec.ExtensionLaunchCapability,
            PreExecute =
            [
                new CommandSpec
                {
                    Command = baseCompile.Command,
                    Args = baseCompile.Args,
                    UpToDateCheck = new CommandUpToDateCheck
                    {
                        Inputs = ["{appHostFile}", ".", ".aspire/modules/**", "src/main/java/**"],
                        FileExtensions = [".java"],
                        StampFile = Path.Combine(".java-build", ".aspire-compile-stamp")
                    }
                }
            ],
            Execute = baseSpec.Execute
        };

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(specWithCheck, JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot);

        // A dependency bump touches no Java source: the descriptor changes and a differently-named JAR
        // is staged. Neither reaches a check whose inputs are only source roots, so the AppHost keeps
        // running bytecode compiled against the version that is no longer on the classpath.
        var inputs = Assert.Single(spec.PreExecute!).UpToDateCheck!.Inputs;
        foreach (var descriptor in expectedDescriptors)
        {
            Assert.Contains(descriptor.Replace('/', Path.DirectorySeparatorChar), inputs);
        }

        Assert.Contains(Path.Combine(outputDirectoryName, "aspire-deps"), inputs);
    }

    [Fact]
    public void ApplyToRuntimeSpec_ForGradle_UsesTheGradleOutputLayout()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        var wrapper = WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot);

        Assert.Equal("Java (Gradle)", spec.DisplayName);
        AssertWrapperInvocation(
            wrapper,
            workspace.Path,
            // CombineProjectPath returns the path with this platform's separators, so the expected value
            // has to be converted too or the comparison fails on Windows.
            ["-q", "--init-script", JavaAppHostToolchainResolver.GradleInitScriptRelativePath.Replace('/', Path.DirectorySeparatorChar), "aspireCopyDependencies"],
            spec.InstallDependencies!);

        var compile = Assert.Single(spec.PreExecute!);
        Assert.Equal("javac", compile.Command);
        Assert.Equal(
            [
                "--release", "25",
                "-classpath", Path.Combine("build", "aspire-deps", "*"),
                "-sourcepath", $".{Path.PathSeparator}{Path.Combine("src", "main", "java")}",
                "-d", Path.Combine("build", "classes", "java", "main"),
                "@.aspire/modules/sources.txt",
                "{appHostFile}"
            ],
            compile.Args);

        Assert.Equal("java", spec.Execute.Command);
        Assert.Equal(
            ["-cp", $"{Path.Combine("build", "classes", "java", "main")}{Path.PathSeparator}{Path.Combine("build", "aspire-deps", "*")}", "AppHost"],
            spec.Execute.Args);
    }

    [Fact]
    public void ApplyToRuntimeSpec_PreservesTheExtensionLaunchCapabilitySoTheAppHostStaysDebuggable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), workspace.WorkspaceRoot);

        Assert.Equal("java", spec.ExtensionLaunchCapability);
    }

    [Theory]
    [InlineData(true, "mvnw", "mvn -N wrapper:wrapper")]
    [InlineData(false, "gradlew", "gradle wrapper")]
    public void GetToolInvocation_WithoutAWrapper_IsRejected(bool useMaven, string wrapperName, string generateCommand)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;

        // A globally installed tool is deliberately not used: the wrapper pins the version the repository
        // builds with, and falling back silently would make the AppHost build machine-dependent.
        var ex = Assert.Throws<InvalidOperationException>(
            () => JavaAppHostToolchainResolver.GetToolInvocation(workspace.WorkspaceRoot, workspace.WorkspaceRoot, toolchain));

        Assert.Contains(wrapperName, ex.Message);
        Assert.Contains(generateCommand, ex.Message);
    }

    [Theory]
    [InlineData(true, "mvnw", "mvnw.cmd")]
    [InlineData(false, "gradlew", "gradlew.bat")]
    public void GetToolInvocation_WithAWrapper_UsesIt(bool useMaven, string wrapperName, string windowsWrapperName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;

        var expectedWrapper = OperatingSystem.IsWindows() ? windowsWrapperName : wrapperName;
        var wrapperPath = Path.Combine(workspace.Path, expectedWrapper);
        File.WriteAllText(wrapperPath, "");

        var invocation = JavaAppHostToolchainResolver.GetToolInvocation(workspace.WorkspaceRoot, workspace.WorkspaceRoot, toolchain);

        if (OperatingSystem.IsWindows())
        {
            // The wrappers are batch files, which produce no output when launched directly with
            // redirected stdout, so the command interpreter runs them instead. "call" leads so the
            // wrapper is never the first token: cmd strips the first and last quote on the line when
            // the first token is quoted, which would mangle a path containing a space.
            Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", invocation.Command);
            Assert.Equal(["/c", "call", expectedWrapper], invocation.PrefixArgs);
        }
        else
        {
            // Run through "sh" rather than executed directly so a wrapper checked out without its
            // executable bit still works. The absolute path is kept because the process starts without
            // a shell, so a bare "mvnw" would be looked up on PATH and never found.
            Assert.Equal("sh", invocation.Command);
            Assert.Equal([wrapperPath], invocation.PrefixArgs);
        }
    }

    [Theory]
    [InlineData(true, "mvnw", "mvnw.cmd")]
    [InlineData(false, "gradlew", "gradlew.bat")]
    public void GetToolInvocation_InAMultiModuleBuild_UsesTheWrapperAtTheBuildRoot(bool useMaven, string wrapperName, string windowsWrapperName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;

        // The standard multi-module layout: one wrapper beside the aggregator POM or settings file at
        // the repository root, and the AppHost in a module that carries only its own build file.
        // Requiring a wrapper beside the module would reject every such repository.
        WriteBuildRootMarker(workspace.Path, useMaven);
        var expectedWrapper = OperatingSystem.IsWindows() ? windowsWrapperName : wrapperName;
        var rootWrapperPath = Path.Combine(workspace.Path, expectedWrapper);
        File.WriteAllText(rootWrapperPath, "");

        var moduleDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "apphost"));
        File.WriteAllText(Path.Combine(moduleDirectory.FullName, useMaven ? "pom.xml" : "build.gradle"), "");

        var invocation = JavaAppHostToolchainResolver.GetToolInvocation(moduleDirectory, moduleDirectory, toolchain);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(["/c", "call", Path.Combine("..", expectedWrapper)], invocation.PrefixArgs);
        }
        else
        {
            Assert.Equal([rootWrapperPath], invocation.PrefixArgs);
        }
    }

    [Theory]
    [InlineData(true, "mvnw", "mvnw.cmd")]
    [InlineData(false, "gradlew", "gradlew.bat")]
    public void GetToolInvocation_DoesNotCrossACheckoutBoundaryLookingForAWrapper(bool useMaven, string wrapperName, string windowsWrapperName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var toolchain = useMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;

        // A wrapper in an outer repository must not be adopted by an inner checkout: a submodule or a
        // nested clone pins its own build tool version, and the outer one is a different project.
        WriteBuildRootMarker(workspace.Path, useMaven);
        File.WriteAllText(Path.Combine(workspace.Path, OperatingSystem.IsWindows() ? windowsWrapperName : wrapperName), "");

        var innerCheckout = Directory.CreateDirectory(Path.Combine(workspace.Path, "inner"));
        Directory.CreateDirectory(Path.Combine(innerCheckout.FullName, ".git"));
        File.WriteAllText(Path.Combine(innerCheckout.FullName, useMaven ? "pom.xml" : "build.gradle"), "");

        var ex = Assert.Throws<InvalidOperationException>(
            () => JavaAppHostToolchainResolver.GetToolInvocation(innerCheckout, innerCheckout, toolchain));

        Assert.Contains(innerCheckout.FullName, ex.Message);
    }

    private static void WriteBuildRootMarker(string directory, bool useMaven)
    {
        if (useMaven)
        {
            File.WriteAllText(Path.Combine(directory, "pom.xml"), "");
        }
        else
        {
            File.WriteAllText(Path.Combine(directory, "settings.gradle"), "");
        }
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_ForGradle_WritesTheInitScriptAndOverwritesAStaleOne()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        var scriptPath = Path.Combine(workspace.Path, ".aspire", "aspire-gradle-init.gradle");

        // The .aspire directory does not exist yet, which is why this is not a RuntimeSpec migration file.
        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), CancellationToken.None);

        Assert.Contains("aspireCopyDependencies", await File.ReadAllTextAsync(scriptPath));

        await File.WriteAllTextAsync(scriptPath, "// stale");
        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), CancellationToken.None);

        Assert.Contains("aspireCopyDependencies", await File.ReadAllTextAsync(scriptPath));
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_ForGradle_StagesWithSyncSoUpgradedDependenciesDoNotAccumulate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");

        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot), CancellationToken.None);

        var script = await File.ReadAllTextAsync(Path.Combine(workspace.Path, ".aspire", "aspire-gradle-init.gradle"));

        // The whole directory is the classpath, so a Copy would leave the previous version of an
        // upgraded dependency behind and load it alongside the new one.
        Assert.Contains("tasks.register(\"aspireCopyDependencies\", Sync)", script);
    }

    [Fact]
    public async Task ClearStagedDependencies_ForMaven_RemovesPreviouslyStagedDependencies()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        var dependencyDirectory = Path.Combine(workspace.Path, "target", "aspire-deps");
        Directory.CreateDirectory(dependencyDirectory);
        await File.WriteAllTextAsync(Path.Combine(dependencyDirectory, "library-1.0.jar"), "");

        JavaAppHostToolchainResolver.ClearStagedDependencies(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot));

        // dependency:copy-dependencies only ever adds, so library-1.0.jar would survive an upgrade to
        // library-2.0.jar and both would be on the AppHost's dir/* classpath.
        Assert.False(Directory.Exists(dependencyDirectory));
    }

    [Fact]
    public void ClearStagedDependencies_ForMaven_WithNothingStagedYet_DoesNotThrow()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");

        JavaAppHostToolchainResolver.ClearStagedDependencies(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot));

        Assert.False(Directory.Exists(Path.Combine(workspace.Path, "target")));
    }

    [Fact]
    public async Task ClearStagedDependencies_ForGradle_LeavesTheDirectoryToTheSyncTask()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");

        var dependencyDirectory = Path.Combine(workspace.Path, "build", "aspire-deps");
        Directory.CreateDirectory(dependencyDirectory);
        await File.WriteAllTextAsync(Path.Combine(dependencyDirectory, "library-1.0.jar"), "");

        JavaAppHostToolchainResolver.ClearStagedDependencies(JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot));

        // Sync prunes the destination itself and stays incremental, so deleting it here would only
        // force Gradle to recopy everything.
        Assert.True(Directory.Exists(dependencyDirectory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureToolchainFilesExistAsync_ForANonGradleToolchain_WritesNothing(bool useJavac)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        if (!useJavac)
        {
            File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        }

        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(
            JavaAppHostToolchainResolver.Resolve(workspace.WorkspaceRoot),
            CancellationToken.None);

        Assert.Empty(workspace.WorkspaceRoot.GetDirectories());
    }

    [Theory]
    [InlineData("pom.xml", true)]
    [InlineData("build.gradle", false)]
    public void Resolve_WithTheConventionalSourceLayout_FindsTheBuildFileAtTheProjectRoot(string buildFileName, bool expectMaven)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var expected = expectMaven ? JavaAppHostToolchain.Maven : JavaAppHostToolchain.Gradle;
        File.WriteAllText(Path.Combine(workspace.Path, buildFileName), "");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "main", "java"));

        // src/main/java is a build tool's source root by convention, so the build file above it is
        // this project's, not an unrelated one that happens to contain the AppHost.
        var resolution = JavaAppHostToolchainResolver.Resolve(appHostDirectory);

        Assert.Equal(expected, resolution.Toolchain);
        Assert.Equal(workspace.Path, resolution.ProjectDirectory.FullName);
    }

    [Fact]
    public void Resolve_WithABuildFileThreeLevelsUpThatIsNotASourceRoot_UsesJavac()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "a", "b", "c"));

        Assert.Equal(JavaAppHostToolchain.Javac, JavaAppHostToolchainResolver.Resolve(appHostDirectory).Toolchain);
    }

    [Fact]
    public void ApplyToRuntimeSpec_WithTheConventionalSourceLayout_PointsTheToolAtTheProjectRoot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "pom.xml"), "<project />");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "main", "java"));

        var resolution = JavaAppHostToolchainResolver.Resolve(appHostDirectory);
        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), resolution, appHostDirectory);

        // Commands run from the AppHost directory, so Maven has to be pointed back at the project root
        // and the classpath has to climb back out of src/main/java.
        var toProjectRoot = Path.Combine("..", "..", "..");
        Assert.Contains("-f", spec.InstallDependencies!.Args);
        Assert.Contains(Path.Combine(toProjectRoot, "pom.xml"), spec.InstallDependencies.Args);

        // -DoutputDirectory is deliberately *not* rewritten: Maven resolves a relative outputDirectory
        // against the project's base directory, not the working directory, so climbing out of
        // src/main/java here would stage the jars three levels above the project.
        Assert.Contains($"-DoutputDirectory={Path.Combine("target", "aspire-deps")}", spec.InstallDependencies.Args);

        // javac and java both resolve their paths against the working directory, so these do climb out.
        Assert.Equal(
            [
                "--release", "25",
                "-classpath", Path.Combine(toProjectRoot, "target", "aspire-deps", "*"),
                "-sourcepath", $".{Path.PathSeparator}{Path.Combine("src", "main", "java")}",
                "-d", Path.Combine(toProjectRoot, "target", "classes"),
                "@.aspire/modules/sources.txt",
                "{appHostFile}"
            ],
            Assert.Single(spec.PreExecute!).Args);
        Assert.Equal(
            [
                "-cp",
                $"{Path.Combine(toProjectRoot, "target", "classes")}{Path.PathSeparator}{Path.Combine(toProjectRoot, "target", "aspire-deps", "*")}",
                "AppHost"
            ],
            spec.Execute.Args);
    }

    [Fact]
    public async Task EnsureToolchainFilesExistAsync_WithTheConventionalSourceLayout_WritesTheScriptNextToTheBuildFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.Path, "build.gradle"), "");
        WriteWrapper(workspace.Path, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "src", "main", "java"));

        var resolution = JavaAppHostToolchainResolver.Resolve(appHostDirectory);
        await JavaAppHostToolchainResolver.EnsureToolchainFilesExistAsync(resolution, CancellationToken.None);

        // The --init-script argument is resolved relative to the AppHost directory, so the script has to
        // be where that argument points: alongside the build file it augments.
        var scriptPath = Path.Combine(workspace.Path, ".aspire", "aspire-gradle-init.gradle");
        Assert.Contains("aspireCopyDependencies", await File.ReadAllTextAsync(scriptPath));

        var spec = JavaAppHostToolchainResolver.ApplyToRuntimeSpec(CreateJavacRuntimeSpec(), resolution, appHostDirectory);
        var initScriptArgument = spec.InstallDependencies!.Args[Array.IndexOf(spec.InstallDependencies.Args, "--init-script") + 1];
        Assert.Equal(scriptPath, Path.GetFullPath(Path.Combine(appHostDirectory.FullName, initScriptArgument)));
    }
}
