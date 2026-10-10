// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.Shared;

namespace Aspire.Hosting.Java;

/// <summary>
/// Resolves Java build tools from project files.
/// </summary>
internal static class JavaBuildToolResolver
{
    private static readonly string[] s_gradleBuildFileNames =
    [
        "build.gradle",
        "build.gradle.kts",
        "settings.gradle",
        "settings.gradle.kts"
    ];

    /// <summary>
    /// Returns the build tool declared by files in <paramref name="appDirectory"/>, or
    /// <see langword="null"/> when none is declared.
    /// </summary>
    internal static JavaBuildTool? Detect(
        string appDirectory,
        string resourceName,
        Func<string, Exception> createAmbiguityException)
    {
        var hasMaven = File.Exists(Path.Combine(appDirectory, "pom.xml"));
        var hasGradle = s_gradleBuildFileNames.Any(fileName => File.Exists(Path.Combine(appDirectory, fileName)));

        // Ambiguous projects are rejected rather than guessed. Maven-first detection made publish produce
        // a different artifact than run mode for the same directory, while an explicit build or launch API
        // records the author's choice for both paths.
        if (hasMaven && hasGradle)
        {
            throw createAmbiguityException(
                $"Directory '{appDirectory}' contains both Maven and Gradle build files, so the build tool for resource '{resourceName}' is ambiguous. " +
                "Use AddJavaApp and call WithMavenBuild, WithGradleBuild, WithMavenGoal, or WithGradleTask to choose one explicitly.");
        }

        return (hasMaven, hasGradle) switch
        {
            (true, false) => JavaBuildTool.Maven,
            (false, true) => JavaBuildTool.Gradle,
            _ => null
        };
    }

    /// <summary>
    /// Resolves the wrapper selected for a resource on the requested execution platform.
    /// </summary>
    /// <remarks>
    /// The application's own directory wins, then the search walks up to the build root. A Gradle
    /// multi-project build has exactly one <c>gradlew</c>, next to the <c>settings.gradle</c> that
    /// declares the subprojects, and Maven multi-module repositories keep <c>mvnw</c> next to the
    /// aggregator POM — so a resource pointed at a module would otherwise never find a wrapper.
    /// <para>
    /// An ancestor only qualifies when it also holds that tool's build-root marker, which keeps an
    /// unrelated wrapper somewhere higher up the filesystem from being picked. The walk stops after
    /// the directory holding <c>.git</c> so a submodule or nested checkout uses its own wrapper
    /// rather than the outer repository's.
    /// </para>
    /// </remarks>
    internal static string ResolveWrapperPath(JavaAppResource resource, JavaBuildTool tool, bool isWindows)
    {
        if (resource.TryGetLastAnnotation<WrapperAnnotation>(out var wrapper))
        {
            return wrapper.WrapperPath;
        }

        var wrapperName = GetDefaultWrapperName(tool, isWindows);
        var appDirectory = resource.WorkingDirectory;

        return PathNormalizer.NormalizePathForCurrentPlatform(
            JavaWrapper.Find(appDirectory, wrapperName, ToWrapperTool(tool)) ?? Path.Combine(appDirectory, wrapperName));
    }

    /// <summary>
    /// Returns the conventional wrapper name for a build tool on the requested execution platform.
    /// </summary>
    internal static string GetDefaultWrapperName(JavaBuildTool tool, bool isWindows) => (tool, isWindows) switch
    {
        (JavaBuildTool.Maven, true) => "mvnw.cmd",
        (JavaBuildTool.Maven, false) => "mvnw",
        (JavaBuildTool.Gradle, true) => "gradlew.bat",
        (JavaBuildTool.Gradle, false) => "gradlew",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null)
    };

    private static JavaWrapperTool ToWrapperTool(JavaBuildTool tool) => tool switch
    {
        JavaBuildTool.Maven => JavaWrapperTool.Maven,
        JavaBuildTool.Gradle => JavaWrapperTool.Gradle,
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null)
    };
}
