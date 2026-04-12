// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Publishing;

public sealed class ContainerRegistryMirrorMsBuildTests
{
    [Fact]
    public async Task RegistryMirrorTargetsFilePath_IsStableAcrossConcurrentCalls()
    {
        var mirrorOptions = new ContainerRegistryMirrorOptions
        {
            Mirrors =
            {
                ["mcr.microsoft.com"] = "docker.example.com/mcr-remote"
            }
        };

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var containerRuntime = new FakeContainerRuntime();

        string? path;

        using (var imageManager = new ResourceContainerImageManager(
            NullLogger<ResourceContainerImageManager>.Instance,
            containerRuntime,
            serviceProvider,
            Options.Create(mirrorOptions)))
        {
            var tasks = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => imageManager.GetRegistryMirrorTargetsFilePath()))
                .ToArray();

            await Task.WhenAll(tasks);

            var paths = tasks.Select(t => t.Result).ToArray();
            Assert.All(paths, p => Assert.False(string.IsNullOrWhiteSpace(p)));

            path = paths[0];
            Assert.All(paths, p => Assert.Equal(path, p));

            var pathAgain = imageManager.GetRegistryMirrorTargetsFilePath();
            Assert.Equal(path, pathAgain);

            Assert.True(File.Exists(path));
        }

        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RegistryMirrorTargetsOverrideContainerBaseImage()
    {
        var options = new ContainerRegistryMirrorOptions
        {
            Mirrors =
            {
                ["mcr.microsoft.com"] = "docker.artifactory.example.com/mcr-remote"
            }
        };

        var (containerBaseImage, mirrorTargetsFileContent) = await RunMsBuildAsync(
            baseImage: "mcr.microsoft.com/dotnet/runtime:8.0",
            mirrorOptions: options);

        Assert.Equal("docker.artifactory.example.com/mcr-remote/dotnet/runtime:8.0", containerBaseImage);

        // Basic sanity checks on the generated targets file
        Assert.Contains("<Target Name=\"_AspireOverrideContainerBaseRegistry\"", mirrorTargetsFileContent);
        Assert.Contains("AfterTargets=\"ComputeContainerBaseImage\"", mirrorTargetsFileContent);
        Assert.Contains("BeforeTargets=\"ComputeContainerConfig\"", mirrorTargetsFileContent);
        Assert.Contains("ContainerBaseImage.Replace('mcr.microsoft.com'", mirrorTargetsFileContent);
    }

    [Fact]
    public async Task RegistryMirrorTargetsOverrideContainerBaseImage_WithMultipleMappings()
    {
        var options = new ContainerRegistryMirrorOptions
        {
            Mirrors =
            {
                ["mcr.microsoft.com"] = "docker.artifactory.example.com/mcr-remote",
                ["ghcr.io"] = "docker.artifactory.example.com/ghcr-remote",
            }
        };

        var (containerBaseImage, mirrorTargetsFileContent) = await RunMsBuildAsync(
            baseImage: "mcr.microsoft.com/dotnet/runtime:8.0",
            mirrorOptions: options);

        Assert.Equal("docker.artifactory.example.com/mcr-remote/dotnet/runtime:8.0", containerBaseImage);
        Assert.Contains("ContainerBaseImage.Replace('mcr.microsoft.com'", mirrorTargetsFileContent);
        Assert.Contains("ContainerBaseImage.Replace('ghcr.io'", mirrorTargetsFileContent);
    }

    [Fact]
    public async Task RegistryMirrorTargetsDoNotOverrideContainerBaseImage_WhenNoMappings()
    {
        var options = new ContainerRegistryMirrorOptions();

        var (containerBaseImage, mirrorTargetsFileContent) = await RunMsBuildAsync(
            baseImage: "mcr.microsoft.com/dotnet/runtime:8.0",
            mirrorOptions: options);

        Assert.Equal("mcr.microsoft.com/dotnet/runtime:8.0", containerBaseImage);

        // Targets file is still valid XML even with no mirror entries.
        _ = XDocument.Parse(mirrorTargetsFileContent);
    }

    [Fact]
    public async Task RegistryMirrorTargetsEscapeXmlAndMsBuildSpecialCharacters()
    {
        var options = new ContainerRegistryMirrorOptions
        {
            Mirrors =
            {
                ["mcr.microsoft.com"] = "docker.example.com/mcr-remote&proxy/it's"
            }
        };

        // These characters are not valid in container image names, but we still ensure the generated .targets file
        // remains well-formed XML and MSBuild-escaped.
        var mirrorTargetsPath = InvokeWriteRegistryMirrorTargetsFile(options);
        try
        {
            var mirrorTargetsFileContent = await File.ReadAllTextAsync(mirrorTargetsPath);
            _ = XDocument.Parse(mirrorTargetsFileContent);
            Assert.Contains("&amp;", mirrorTargetsFileContent);
            Assert.Contains("it''s", mirrorTargetsFileContent);
        }
        finally
        {
            if (File.Exists(mirrorTargetsPath))
            {
                File.Delete(mirrorTargetsPath);
            }
        }
    }

    [Fact]
    public void MirrorsDictionaryIsCaseInsensitive()
    {
        var options = new ContainerRegistryMirrorOptions();
        options.Mirrors["MCR.MICROSOFT.COM"] = "mirror1";
        options.Mirrors["mcr.microsoft.com"] = "mirror2";

        Assert.Single(options.Mirrors);
        Assert.Equal("mirror2", options.Mirrors["MCR.MICROSOFT.COM"]);
    }

    [Fact]
    public void WriteRegistryMirrorTargetsFile_ThrowsWhenTempDirectoryIsNotWritable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var tempDirectory = new TestTempDirectory();
        var nonWritableTemp = Path.Combine(tempDirectory.Path, "nonwritable");
        Directory.CreateDirectory(nonWritableTemp);

        UnixFileMode? originalMode = null;

        try
        {
            originalMode = File.GetUnixFileMode(nonWritableTemp);
            File.SetUnixFileMode(nonWritableTemp, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            var originalTmpDir = Environment.GetEnvironmentVariable("TMPDIR");
            try
            {
                Environment.SetEnvironmentVariable("TMPDIR", nonWritableTemp);

                // Path.GetTempPath() may be cached. If it doesn't respect TMPDIR, skip this check.
                if (!Path.GetTempPath().StartsWith(nonWritableTemp, StringComparison.Ordinal))
                {
                    return;
                }

                var options = new ContainerRegistryMirrorOptions
                {
                    Mirrors =
                    {
                        ["mcr.microsoft.com"] = "docker.example.com/mcr"
                    }
                };

                var ex = Record.Exception(() => InvokeWriteRegistryMirrorTargetsFile(options));
                Assert.NotNull(ex);
            }
            finally
            {
                Environment.SetEnvironmentVariable("TMPDIR", originalTmpDir);
            }
        }
        catch (PlatformNotSupportedException ex)
        {
            // Unix file mode APIs may not be available on all platforms.
            Trace.WriteLine(ex);
        }
        finally
        {
            try
            {
                if (originalMode is UnixFileMode mode)
                {
                    File.SetUnixFileMode(nonWritableTemp, mode);
                }
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // Best effort cleanup.
                Trace.WriteLine(ex);
            }
        }
    }

    private static async Task<(string ContainerBaseImage, string TargetsFileContent)> RunMsBuildAsync(
        string baseImage,
        ContainerRegistryMirrorOptions mirrorOptions)
    {
        using var tempDirectory = new TestTempDirectory();

        var projectPath = Path.Combine(tempDirectory.Path, "App.csproj");
        var outputPath = Path.Combine(tempDirectory.Path, "container-base-image.txt");
        var wrapperTargetsPath = Path.Combine(tempDirectory.Path, "wrapper.targets");

        File.WriteAllText(projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

              <Target Name="ComputeContainerBaseImage">
                <PropertyGroup>
                  <ContainerBaseImage>{EscapeXmlText(baseImage)}</ContainerBaseImage>
                </PropertyGroup>
              </Target>

              <Target Name="ComputeContainerConfig" DependsOnTargets="ComputeContainerBaseImage" />
            </Project>
            """.Replace("\r\n", "\n"));

        var mirrorTargetsPath = InvokeWriteRegistryMirrorTargetsFile(mirrorOptions);

        File.WriteAllText(wrapperTargetsPath,
            $"""
            <Project>
              <Import Project="{EscapeForXmlAttribute(mirrorTargetsPath)}" />
              <Target Name="_AspireMirrorProbeWriteContainerBaseImage" AfterTargets="ComputeContainerConfig">
                <WriteLinesToFile File="{EscapeForXmlAttribute(outputPath)}" Lines="$(ContainerBaseImage)" Overwrite="true" />
              </Target>
            </Project>
            """.Replace("\r\n", "\n"));

        try
        {
            RunDotNetMsBuild(
                workingDirectory: tempDirectory.Path,
                $"msbuild -restore --disable-build-servers -nologo \"{projectPath}\" -t:ComputeContainerConfig -p:CustomAfterMicrosoftCommonTargets=\"{wrapperTargetsPath}\"");

            var containerBaseImage = (await File.ReadAllTextAsync(outputPath)).Trim();
            var targetsFileContent = await File.ReadAllTextAsync(mirrorTargetsPath);

            return (containerBaseImage, targetsFileContent);
        }
        finally
        {
            if (File.Exists(mirrorTargetsPath))
            {
                File.Delete(mirrorTargetsPath);
            }
        }
    }

    private static string InvokeWriteRegistryMirrorTargetsFile(ContainerRegistryMirrorOptions options)
    {
        var path = ResourceContainerImageManager.WriteRegistryMirrorTargetsFile(options);
        Assert.False(string.IsNullOrWhiteSpace(path));
        return path;
    }

    private static void RunDotNetMsBuild(string workingDirectory, string arguments)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(GetDotNetHostPath(), arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                error.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Assert.True(process.WaitForExit(milliseconds: 180_000), $"dotnet msbuild timed out. Output:{Environment.NewLine}{output}{Environment.NewLine}Error:{Environment.NewLine}{error}");
        Assert.True(process.ExitCode == 0, $"dotnet msbuild failed with exit code {process.ExitCode}.{Environment.NewLine}Output:{Environment.NewLine}{output}{Environment.NewLine}Error:{Environment.NewLine}{error}");
    }

    private static string GetDotNetHostPath()
    {
        var repoRoot = MSBuildUtils.GetRepoRoot();
        var candidate = Path.Combine(repoRoot, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    private static string EscapeForXmlAttribute(string value)
        => value.Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");

    private static string EscapeXmlText(string value)
        => value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
}
