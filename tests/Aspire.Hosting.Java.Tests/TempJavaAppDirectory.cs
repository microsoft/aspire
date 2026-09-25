// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Java.Tests;

/// <summary>
/// Creates a securely-created temporary directory that stands in for a Java application directory.
/// </summary>
internal sealed class TempJavaAppDirectory : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aspire-java-tests");
    private readonly DirectoryInfo _directory;

    /// <param name="withWrappers">
    /// Whether to seed both wrapper scripts. Aspire requires a wrapper, so a project that ships one is the
    /// normal case; pass <see langword="false"/> to exercise the rejection.
    /// </param>
    /// <param name="directoryName">
    /// Name to give the application's own directory. The default random name is fine for most tests, but
    /// the name a project is imported under in the IDE is only trusted when it matches the directory, so
    /// tests covering that have to control it.
    /// </param>
    public TempJavaAppDirectory(bool withWrappers = true, string? directoryName = null)
    {
        _directory = directoryName is null
            ? _root
            : Directory.CreateDirectory(System.IO.Path.Combine(_root.FullName, directoryName));

        if (withWrappers)
        {
            WriteWrapper(OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw");
            WriteWrapper(OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        }
    }

    public string Path => _directory.FullName;

    /// <summary>
    /// Writes a file into the directory, creating any intermediate directories.
    /// </summary>
    public string Write(string fileName, string content = "")
    {
        var fullPath = System.IO.Path.Combine(Path, fileName);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);

        return fullPath;
    }

    /// <summary>
    /// Writes a build tool wrapper script into the directory and marks it executable on Unix, matching
    /// what a real <c>mvnw</c>/<c>gradlew</c> checkout looks like.
    /// </summary>
    public string WriteWrapper(string fileName)
    {
        var fullPath = Write(fileName, "#!/bin/sh\nexit 0\n");

        // A real wrapper is never just the script: the properties file next to it is what pins the tool
        // version, and publishing requires it so the distribution can be unpacked in its own image layer.
        var gradle = fileName.Contains("gradle", StringComparison.OrdinalIgnoreCase);
        var supportDirectory = gradle ? "gradle" : ".mvn";
        var propertiesName = gradle ? "gradle-wrapper.properties" : "maven-wrapper.properties";
        var wrapperDirectory = System.IO.Path.GetDirectoryName(fullPath)!;

        Directory.CreateDirectory(System.IO.Path.Combine(wrapperDirectory, supportDirectory, "wrapper"));
        File.WriteAllText(
            System.IO.Path.Combine(wrapperDirectory, supportDirectory, "wrapper", propertiesName),
            gradle
                ? "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.14-bin.zip\n"
                : "distributionUrl=https\\://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/3.9.9/apache-maven-3.9.9-bin.zip\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a virus scanner or indexer can briefly hold a handle on Windows, and failing
            // to clean up a temp directory must not fail an otherwise passing test.
        }
    }
}
