// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

using Aspire.TypeSystem;

namespace Aspire.Hosting.CodeGeneration.Java;

/// <summary>
/// Provides language support for Java AppHosts.
/// Implements scaffolding, detection, and runtime configuration.
/// </summary>
internal sealed class JavaLanguageSupport : ILanguageSupport
{
    /// <summary>
    /// The language/runtime identifier for Java.
    /// </summary>
    private const string LanguageId = "java";

    /// <summary>
    /// The code generation target language. This maps to the ICodeGenerator.Language property.
    /// </summary>
    private const string CodeGenTarget = "Java";

    private const string LanguageDisplayName = "Java";

    /// <summary>
    /// AppHost locations, in priority order: the flat single-file layout, and the standard Maven and
    /// Gradle source root that a build-tool project uses.
    /// </summary>
    /// <remarks>
    /// The flat layout is listed first and remains the default so an AppHost that predates build-tool
    /// support keeps working unchanged, and so the common case needs nothing but a JDK.
    /// </remarks>
    private static readonly string[] s_detectionPatterns =
    [
        "AppHost.java",
        "src/main/java/AppHost.java"
    ];

    /// <inheritdoc />
    public string Language => LanguageId;

    /// <inheritdoc />
    public Dictionary<string, string> Scaffold(ScaffoldRequest request)
    {
        var files = new Dictionary<string, string>();

        files[".gitignore"] = """
            .java-build/
            .aspire/
            """;

        files["AppHost.java"] = """
            // Aspire Java AppHost
            // For more information, see: https://aspire.dev
            
            import aspire.*;

            void main(String[] args) throws Exception {
                var builder = DistributedApplication.CreateBuilder(args);

                // Add your resources here, for example:
                // var redis = builder.addRedis("cache");
                // var postgres = builder.addPostgres("db");

                builder.build().run();
            }
            """;

        // Create apphost.run.json with random ports
        var random = request.PortSeed.HasValue
            ? new Random(request.PortSeed.Value)
            : Random.Shared;

        var httpsPort = random.Next(10000, 65000);
        var httpPort = random.Next(10000, 65000);
        var otlpPort = random.Next(10000, 65000);
        var resourceServicePort = random.Next(10000, 65000);

        files["apphost.run.json"] = $$"""
            {
              "profiles": {
                "https": {
                  "applicationUrl": "https://localhost:{{httpsPort}};http://localhost:{{httpPort}}",
                  "environmentVariables": {
                    "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "https://localhost:{{otlpPort}}",
                    "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "https://localhost:{{resourceServicePort}}"
                  }
                }
              }
            }
            """;

        // Without a pom.xml or build.gradle the Java language server treats the folder as an
        // "invisible project" and only puts the workspace root on the source path, so every
        // reference to the generated SDK under .aspire/modules resolves to "cannot be resolved to a
        // type": no completion, no navigation, and no breakpoint binding in the AppHost. Declaring
        // both source roots is what makes a build-tool-free AppHost a real editing experience.
        // The setting is ignored once a build file exists, because the build tool then owns the
        // project model, so this stays correct if the user later adopts Maven or Gradle.
        // https://github.com/redhat-developer/vscode-java/wiki/Java-Project-Settings
        files[".vscode/settings.json"] = """
            {
              "java.project.sourcePaths": [
                ".",
                ".aspire/modules"
              ],
              "java.compile.nullAnalysis.mode": "disabled"
            }
            """;

        return files;
    }

    /// <inheritdoc />
    public DetectionResult Detect(string directoryPath)
    {
        foreach (var pattern in s_detectionPatterns)
        {
            // The patterns are written with forward slashes because they are also a wire contract,
            // so they have to be translated before touching the file system on Windows.
            var relativePath = pattern.Replace('/', Path.DirectorySeparatorChar);

            if (File.Exists(Path.Combine(directoryPath, relativePath)))
            {
                return DetectionResult.Found(LanguageId, relativePath);
            }
        }

        return DetectionResult.NotFound;
    }

    /// <summary>
    /// Directory that the generated SDK sources and the AppHost are compiled into.
    /// </summary>
    private const string BuildOutputDirectory = ".java-build";

    /// <summary>
    /// Compiler options used to build the AppHost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scaffolded AppHost is a compact source file with an instance <c>main</c> method, which
    /// requires Java 25. That feature was previewed in Java 21 through 24 (JEP 445, 463, 477, and
    /// 495) and finalized in Java 25 by <see href="https://openjdk.org/jeps/512">JEP 512</see>, so
    /// <c>--enable-preview</c> is deliberately absent: passing it here compiles no preview feature
    /// and only risks stamping the class files with the preview minor version (65535), which binds
    /// them to one exact JDK release and forces the flag at run time too.
    /// </para>
    /// <para>
    /// <c>--release</c> is used rather than <c>--source</c> because only <c>--release</c> also
    /// constrains the visible API surface. With <c>--source</c> alone a newer JDK still compiles
    /// against its own class library, so an AppHost can bind to APIs that do not exist in Java 25
    /// and then fail at run time on a conforming Java 25 runtime.
    /// </para>
    /// </remarks>
    private static readonly string[] s_javacOptions = ["--release", "25"];

    /// <summary>
    /// Argument file listing the generated SDK sources, produced by the code generator.
    /// </summary>
    /// <remarks>
    /// Passed to <c>javac</c> as an <c>@</c> argument file. javac expands these itself, so this
    /// works without a shell and stays well under the command-line length limit even though the
    /// generated SDK is hundreds of files.
    /// </remarks>
    private const string GeneratedSourcesListPath = $"{GeneratedSourcesDirectory}/sources.txt";

    /// <summary>
    /// Directory the generated SDK sources are written to.
    /// </summary>
    private const string GeneratedSourcesDirectory = ".aspire/modules";

    /// <summary>
    /// Class that the scaffolded AppHost compiles to.
    /// </summary>
    /// <remarks>
    /// The AppHost is declared in the default package, so this is also its fully qualified name.
    /// </remarks>
    private const string AppHostClassName = "AppHost";

    /// <summary>
    /// Name of the file written after a successful compile, used to skip the next one.
    /// </summary>
    internal const string CompileStampFileName = ".aspire-compile-stamp";

    /// <summary>
    /// Sets the up-to-date check that lets an unchanged AppHost skip <c>javac</c> entirely when supported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// javac given an explicit list of sources recompiles every one of them, so without this the
    /// generated SDK — several hundred files — is recompiled on every launch even when nothing
    /// changed. Every other supported language either has an incremental compiler of its own or no
    /// compile step at all, so this is what brings Java to the same startup cost.
    /// </para>
    /// <para>
    /// The inputs are exactly javac's source roots: the AppHost file, the sources beside it, the
    /// generated SDK, and <c>src/main/java</c> for the layout where the AppHost sits at the project
    /// root. The AppHost directory is recursive because javac is given no <c>-sourcepath</c>, so its
    /// source path defaults to the user class path and therefore to the AppHost directory: a helper
    /// class in a package beside the AppHost is compiled implicitly, and rewriting it in place moves
    /// no ancestor's timestamp. The scan prunes the trees that cannot hold a package - dot
    /// directories, <c>node_modules</c>, and javac's own output - so the sibling trees in a typical
    /// solution do not give back the time this saves.
    /// </para>
    /// </remarks>
    /// <param name="commandSpec">The compile command to update.</param>
    /// <param name="classOutputDirectory">Directory javac writes classes to, which is where the stamp lives.</param>
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The installed CLI roots the force-shared contract when this property exists.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The installed CLI roots the force-shared contract when this property exists.")]
    internal static void SetCompileUpToDateCheckIfSupported(object commandSpec, string classOutputDirectory)
    {
        // Aspire.TypeSystem is force-shared from the installed CLI. A newer codegen assembly can
        // therefore run against an older CommandSpec that has the same assembly identity but does not
        // expose this additive property. Probe by name so the optimization is skipped and the older CLI
        // compiles on every launch rather than failing to load the Java language support.
        var upToDateCheckProperty = commandSpec.GetType().GetProperty(nameof(CommandSpec.UpToDateCheck));

        if (upToDateCheckProperty is null)
        {
            return;
        }

        var expectedTypeName = $"{typeof(CommandSpec).Namespace}.{nameof(CommandUpToDateCheck)}";
        var upToDateCheckType = upToDateCheckProperty.PropertyType;
        var inputsProperty = upToDateCheckType.GetProperty(nameof(CommandUpToDateCheck.Inputs));
        var outputsProperty = upToDateCheckType.GetProperty(nameof(CommandUpToDateCheck.Outputs));
        var fileExtensionsProperty = upToDateCheckType.GetProperty(nameof(CommandUpToDateCheck.FileExtensions));
        var stampFileProperty = upToDateCheckType.GetProperty(nameof(CommandUpToDateCheck.StampFile));

        // Outputs was added after UpToDateCheck. An older force-shared contract can expose the check
        // but not this member; in that case compile every launch instead of caching without proving
        // that AppHost.class still exists.
        if (outputsProperty is null)
        {
            return;
        }

        if (upToDateCheckProperty.SetMethod is null ||
            !upToDateCheckProperty.SetMethod.IsPublic ||
            upToDateCheckType.Assembly != typeof(CommandSpec).Assembly ||
            upToDateCheckType.FullName != expectedTypeName ||
            upToDateCheckType.IsAbstract ||
            upToDateCheckType.GetConstructor(Type.EmptyTypes) is null ||
            inputsProperty?.PropertyType != typeof(string[]) ||
            inputsProperty.SetMethod is null ||
            !inputsProperty.SetMethod.IsPublic ||
            outputsProperty.PropertyType != typeof(string[]) ||
            outputsProperty.SetMethod is null ||
            !outputsProperty.SetMethod.IsPublic ||
            fileExtensionsProperty?.PropertyType != typeof(string[]) ||
            fileExtensionsProperty.SetMethod is null ||
            !fileExtensionsProperty.SetMethod.IsPublic ||
            stampFileProperty?.PropertyType != typeof(string) ||
            stampFileProperty.SetMethod is null ||
            !stampFileProperty.SetMethod.IsPublic)
        {
            throw new MissingMemberException(
                $"The runtime {nameof(CommandSpec.UpToDateCheck)} contract does not match {expectedTypeName}.");
        }

        var upToDateCheck = Activator.CreateInstance(upToDateCheckType)
            ?? throw new MissingMemberException($"The runtime type {expectedTypeName} could not be created.");

        inputsProperty.SetValue(upToDateCheck, new[]
        {
            "{appHostFile}",
            "./**",
            $"{GeneratedSourcesDirectory}/**",
            "src/main/java/**"
        });
        outputsProperty.SetValue(upToDateCheck, new[]
        {
            Path.Combine(classOutputDirectory, $"{AppHostClassName}.class")
        });
        // Only sources are inputs. Without this the .class files javac writes beside the sources in
        // the flat layout would invalidate the very check they were produced under.
        fileExtensionsProperty.SetValue(upToDateCheck, new[] { ".java" });
        stampFileProperty.SetValue(upToDateCheck, Path.Combine(classOutputDirectory, CompileStampFileName));
        upToDateCheckProperty.SetValue(commandSpec, upToDateCheck);
    }

    /// <inheritdoc />
    public RuntimeSpec GetRuntimeSpec()
    {
        var compile = new CommandSpec
        {
            // No shell. javac creates the destination directory itself, so there is nothing
            // left that needed one, and running without a shell means arguments are not
            // re-split: a project under a path such as "C:\My Projects" works unchanged, on
            // Windows and Unix alike, from a single spec.
            Command = "javac",
            Args = [.. s_javacOptions, "-d", BuildOutputDirectory, $"@{GeneratedSourcesListPath}", "{appHostFile}"]
        };
        SetCompileUpToDateCheckIfSupported(compile, BuildOutputDirectory);

        return new RuntimeSpec
        {
            Language = LanguageId,
            DisplayName = LanguageDisplayName,
            CodeGenLanguage = CodeGenTarget,
            DetectionPatterns = s_detectionPatterns,
            // Compilation is a pre-execute step rather than part of Execute so that Execute is a plain
            // JVM launch. That is what lets the AppHost be debugged (the IDE starts the JVM itself and
            // would otherwise start a shell), and it lets --no-build skip the compile.
            // A Maven or Gradle AppHost replaces both commands via JavaAppHostToolchainResolver.
            InstallDependencies = null,
            PreExecute = [compile],
            // Debugging the AppHost itself goes through the same Java debug adapter the resources use.
            // The CLI only takes this path when the extension reports the capability, so a CLI-only
            // run is unaffected.
            ExtensionLaunchCapability = LanguageId,
            Execute = new CommandSpec
            {
                Command = "java",
                // {args} is deliberately absent. When no argument contains that placeholder the CLI
                // appends its arguments as separate argv entries, whereas substituting the placeholder
                // joins them into a single space-separated string the AppHost would have to re-split.
                Args = ["-cp", BuildOutputDirectory, AppHostClassName]
            }
        };
    }
}
