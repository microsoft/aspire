// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// The build tools that ship a checked-in wrapper script.
/// </summary>
internal enum JavaWrapperTool
{
    Maven,
    Gradle
}

/// <summary>
/// Locates and launches the <c>mvnw</c>/<c>gradlew</c> wrapper checked into a Java project. Shared by the CLI
/// and the Java hosting integration so both resolve and invoke the wrapper identically.
/// </summary>
internal static class JavaWrapper
{
    /// <summary>
    /// Locates the wrapper named <paramref name="wrapperName"/>, preferring <paramref name="projectDirectory"/>
    /// and otherwise walking up to the build root.
    /// </summary>
    /// <remarks>
    /// A Gradle multi-project build has exactly one <c>gradlew</c>, beside the <c>settings.gradle</c> that
    /// declares the subprojects, and a Maven multi-module repository keeps <c>mvnw</c> beside the aggregator
    /// POM. A project that is one of those modules carries only its own build file, so requiring a wrapper
    /// next to it would reject the standard layout outright.
    /// <para>
    /// An ancestor only qualifies when it also holds that tool's build-root marker and is not world-writable,
    /// and the walk stops at the directory holding <c>.git</c> so a submodule or nested clone uses its own
    /// wrapper rather than the outer repository's.
    /// </para>
    /// </remarks>
    public static string? Find(string projectDirectory, string wrapperName, JavaWrapperTool tool)
    {
        var isProjectDirectory = true;

        for (var directory = SafeDirectoryInfo(projectDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, wrapperName);

            // The project directory is named by the AppHost, so a wrapper beside it is the developer's
            // own instruction and needs no further qualification. Ancestors are inferred instead.
            if (File.Exists(candidate)
                && (isProjectDirectory
                    || (IsBuildRoot(directory.FullName, tool)
                        && !IsWorldWritable(directory)
                        && !IsWorldWritable(new FileInfo(candidate)))))
            {
                return candidate;
            }

            // A worktree or submodule records .git as a file rather than a directory, so both count.
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return null;
            }

            isProjectDirectory = false;
        }

        return null;
    }

    /// <summary>
    /// Returns the command and leading arguments that launch the wrapper, with the wrapper path made relative
    /// to <paramref name="workingDirectory"/> on Windows.
    /// </summary>
    /// <remarks>
    /// On Unix the wrapper is invoked through <c>sh</c> instead of being executed directly. Git does not record
    /// an executable bit on Windows, so a repository committed from there checks out <c>mvnw</c> and
    /// <c>gradlew</c> as mode 644 and executing them fails with "permission denied". Both are POSIX shell
    /// scripts, so <c>sh</c> runs them either way. The container build does the same for the same reason, and
    /// run mode has to match or an identical checkout fails on Linux and macOS while succeeding inside the image.
    /// The absolute path is kept because the process is started without a shell, so a bare <c>mvnw</c> would be
    /// looked up on <c>PATH</c> and never found in the project directory.
    /// <para>
    /// On Windows the wrappers are the <c>mvnw.cmd</c> and <c>gradlew.bat</c> batch files, which <c>sh</c> cannot
    /// run. They are launched through the command interpreter because a batch file started with redirected
    /// stdout can silently produce no output; <c>NpmRunner</c> hits the same constraint with <c>npm.cmd</c>.
    /// </para>
    /// </remarks>
    public static (string Command, string[] LeadingArgs) GetInvocation(string wrapperPath, string workingDirectory, bool isWindows)
    {
        if (!isWindows)
        {
            return ("sh", [wrapperPath]);
        }

        // Passing the wrapper as a path relative to the working directory keeps it short and usually free of
        // spaces, which matters because cmd.exe strips quotes in a way that does not match how arguments are
        // escaped for it: when the *first* token on the line is quoted, cmd removes that quote and the last
        // one on the line, mangling everything in between.
        var relativeWrapperPath = Path.GetRelativePath(workingDirectory, wrapperPath);

        // A bare "mvnw.cmd" is only found in the working directory while cmd.exe searches it, and
        // NoDefaultCurrentDirectoryInExePath=1 (a common hardening setting) turns that search off, which
        // fails with "'mvnw.cmd' is not recognized". A "." segment makes it a path rather than a name;
        // any path that already has a separator is resolved directly.
        if (!relativeWrapperPath.Contains(Path.DirectorySeparatorChar))
        {
            relativeWrapperPath = Path.Combine(".", relativeWrapperPath);
        }

        // "call" makes the quote stripping unreachable rather than merely unlikely. A wrapper reached through a
        // directory with a space in its name is quoted when the command line is built, and quoting the first
        // token is exactly what triggers the stripping. With "call" ahead of it the first character is never a
        // quote, and "call" is how a batch file is meant to be invoked from another anyway: it returns control
        // and propagates the wrapper's exit code. See the quote-processing rules printed by `cmd /?`.
        return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/c", "call", relativeWrapperPath]);
    }

    private static bool IsBuildRoot(string directory, JavaWrapperTool tool) => tool switch
    {
        // Gradle requires a settings file at the root of a multi-project build; that is the directory
        // the wrapper is generated into. https://docs.gradle.org/current/userguide/multi_project_builds.html
        JavaWrapperTool.Gradle => File.Exists(Path.Combine(directory, "settings.gradle"))
                                  || File.Exists(Path.Combine(directory, "settings.gradle.kts")),
        // A Maven aggregator is itself a project, so its POM is the marker.
        // https://maven.apache.org/guides/introduction/introduction-to-the-pom.html
        JavaWrapperTool.Maven => File.Exists(Path.Combine(directory, "pom.xml")),
        _ => false
    };

    /// <summary>
    /// Returns whether any user on the machine can write to <paramref name="entry"/>.
    /// </summary>
    /// <remarks>
    /// Only inferred ancestors are checked. On a shared machine a project under a world-writable directory
    /// such as <c>/tmp</c> could otherwise pick up a wrapper another user planted beside a <c>pom.xml</c>, and
    /// execute it with the developer's privileges before anything is built. Applied to the wrapper file as well
    /// as its directory, because rewriting a file in place needs write permission on the file rather than on the
    /// directory holding it.
    /// <para>
    /// Group-writable is deliberately not rejected: distributions that enable user private groups pair a umask
    /// of 002 with a group per user, so an ordinary checkout is mode 775 and rejecting it would break wrapper
    /// resolution for a large share of Linux users.
    /// </para>
    /// <para>
    /// Windows uses ACLs that <see cref="UnixFileMode"/> does not describe, and .NET reports
    /// <see cref="UnixFileMode.None"/> there, so the check is skipped.
    /// </para>
    /// </remarks>
    private static bool IsWorldWritable(FileSystemInfo entry)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return entry.UnixFileMode.HasFlag(UnixFileMode.OtherWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A path whose mode cannot be read cannot be shown safe, so treat it as unusable; the caller
            // then falls back to the wrapper beside the project, as if no ancestor wrapper existed.
            return true;
        }
    }

    // Resolution runs while the AppHost is still being authored, so the directory may not exist yet
    // and may be a value the developer has not finished typing.
    private static DirectoryInfo? SafeDirectoryInfo(string path)
    {
        try
        {
            return new DirectoryInfo(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }
}
