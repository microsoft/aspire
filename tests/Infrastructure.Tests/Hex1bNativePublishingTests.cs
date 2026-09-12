// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Infrastructure.Tests;

public sealed class Hex1bNativePublishingTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;

    public Hex1bNativePublishingTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = new TemporaryWorkspace(output, Directory.CreateDirectory(
            Path.Combine(RepoRoot.Path, "artifacts", "tests", nameof(Hex1bNativePublishingTests), Guid.NewGuid().ToString("N"))));
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("win-x64", false)]
    [InlineData("win-x64", true)]
    [InlineData("win-arm64", false)]
    [InlineData("win-arm64", true)]
    [InlineData("", false)]
    [InlineData("linux-x64", true)]
    public async Task NativeAssetsKeepTheirLayoutAndContents(string rid, bool singleFile)
    {
        var project = CreatePublishProject(rid, singleFile, duplicateUnrelatedAsset: false);
        var result = await RunDotNetAsync(["msbuild", project, "-nologo", "-t:CopyTestFiles", "-getItem:NativeCopyLocalItems,ResolvedFileToPublish"]);
        Assert.True(result.ExitCode == 0, result.Output);

        using var document = JsonDocument.Parse(result.Output);
        var items = document.RootElement.GetProperty("Items");
        var published = items.GetProperty("ResolvedFileToPublish").EnumerateArray().ToArray();
        var expected = GetNativePaths(rid).Append("other/OpenConsole.exe").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, published.Select(item => item.GetProperty("RelativePath").GetString()).Order(StringComparer.Ordinal));

        foreach (var item in published)
        {
            var pathInPackage = item.GetProperty("PathInPackage").GetString()!;
            var relativePath = item.GetProperty("RelativePath").GetString()!;
            var source = item.GetProperty("Identity").GetString()!;
            var destination = Path.Combine(_workspace.Path, "publish", relativePath);
            Assert.True(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(destination)), relativePath);

            var isWindowsPty = pathInPackage.StartsWith("runtimes/win-", StringComparison.Ordinal);
            Assert.Equal(isWindowsPty, item.TryGetProperty("ExcludeFromSingleFile", out var excluded) && excluded.GetString() == "true");
        }

        var built = items.GetProperty("NativeCopyLocalItems").EnumerateArray()
            .Select(item => item.GetProperty("DestinationSubPath").GetString()).Order(StringComparer.Ordinal);
        Assert.Equal(rid.Length == 0 ? [] : GetNativePaths(rid).Order(StringComparer.Ordinal), built);
    }

    [Fact]
    public async Task UnrelatedPublishCollisionsStillFail()
    {
        var project = CreatePublishProject("win-x64", singleFile: false, duplicateUnrelatedAsset: true);
        var result = await RunDotNetAsync(["msbuild", project, "-nologo", "-t:CopyTestFiles"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("NETSDK1152", result.Output);
    }

    [Theory]
    [InlineData("win-x64", null)]
    [InlineData("win-arm64", null)]
    [InlineData("linux-x64", null)]
    [InlineData("win-x64", "arm64/OpenConsole.exe")]
    [InlineData("win-arm64", "hex1bpty.exe")]
    public async Task BundlePreservesPtyLayoutAndRejectsMissingSidecars(string rid, string? missingSidecar)
    {
        var artifacts = Path.Combine(_workspace.Path, "artifacts");
        var publish = Path.Combine(artifacts, "bin", "Aspire.Managed", "Release", "net10.0", rid, "publish");
        var executable = rid.StartsWith("win-", StringComparison.Ordinal) ? "aspire-managed.exe" : "aspire-managed";
        var files = new List<string> { executable, "wwwroot/index.html" };
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            files.AddRange(GetNativePaths(rid));
        }

        foreach (var file in files.Append("Aspire.Dashboard.exe").Append("Aspire.TerminalHost.exe"))
        {
            if (file != missingSidecar)
            {
                WriteFile(Path.Combine(publish, file), file);
            }
        }

        var packageRid = rid switch
        {
            "win-x64" => "windows-amd64",
            "win-arm64" => "windows-arm64",
            _ => "linux-amd64"
        };
        var packages = Path.Combine(_workspace.Path, "packages");
        WriteFile(Path.Combine(packages, $"microsoft.developercontrolplane.{packageRid}", "1.0.0", "tools", "dcp"), "dcp");
        var layout = Path.Combine(_workspace.Path, "layout");
        var testAssembly = typeof(Hex1bNativePublishingTests).Assembly.Location;
        var result = await RunDotNetAsync(
            ["exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
             "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
             typeof(Aspire.Tools.CreateLayout.Program).Assembly.Location,
             "--output", layout, "--artifacts", artifacts, "--rid", rid],
            packages);
        if (missingSidecar is not null)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(Path.GetFileName(missingSidecar), result.Output);
            return;
        }

        Assert.True(result.ExitCode == 0, result.Output);

        var managed = Path.Combine(layout, "managed");
        Assert.Equal(files.Order(StringComparer.Ordinal), Directory.GetFiles(managed, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(managed, path).Replace('\\', '/')).Order(StringComparer.Ordinal));
        foreach (var file in files)
        {
            Assert.True(File.ReadAllBytes(Path.Combine(publish, file)).SequenceEqual(File.ReadAllBytes(Path.Combine(managed, file))), file);
        }
    }

    [Fact]
    public void SharedTargetsAreImportedAndShippedForHostingConsumers()
    {
        var repositoryTargets = XDocument.Load(Path.Combine(RepoRoot.Path, "Directory.Build.targets"));
        Assert.Contains(repositoryTargets.Descendants("Import"), import =>
            (string?)import.Attribute("Project") == "$(MSBuildThisFileDirectory)src/Aspire.Hosting/buildTransitive/Aspire.Hosting.Hex1b.targets");
        var hostingTargets = XDocument.Load(Path.Combine(RepoRoot.Path, "src", "Aspire.Hosting", "buildTransitive", "Aspire.Hosting.targets"));
        Assert.Contains(hostingTargets.Descendants("Import"), import => (string?)import.Attribute("Project") == "Aspire.Hosting.Hex1b.targets");
        var hostingProject = XDocument.Load(Path.Combine(RepoRoot.Path, "src", "Aspire.Hosting", "Aspire.Hosting.csproj"));
        Assert.Contains(hostingProject.Descendants("None"), item =>
            (string?)item.Attribute("Include") == @"buildTransitive\Aspire.Hosting.Hex1b.targets" &&
            (string?)item.Attribute("Pack") == "true" &&
            (string?)item.Attribute("PackagePath") == @"buildTransitive\$(DefaultTargetFramework)");
    }

    private string CreatePublishProject(string rid, bool singleFile, bool duplicateUnrelatedAsset)
    {
        var nativeItems = new XElement("ItemGroup");
        foreach (var relativePath in GetNativePaths(rid))
        {
            var pathInPackage = rid.Length == 0 ? relativePath : $"runtimes/{rid}/native/{relativePath}";
            var source = Path.Combine(_workspace.Path, "package", pathInPackage);
            WriteFile(source, pathInPackage);
            nativeItems.Add(new XElement(rid.Length == 0 ? "RuntimeTargetsCopyLocalItems" : "NativeCopyLocalItems",
                new XAttribute("Include", source),
                new XElement("NuGetPackageId", "Hex1b"),
                new XElement("PathInPackage", pathInPackage),
                new XElement("DestinationSubPath", rid.Length == 0 ? pathInPackage : Path.GetFileName(pathInPackage)),
                new XElement("DestinationSubDirectory", rid.Length == 0 ? pathInPackage[..(pathInPackage.LastIndexOf('/') + 1)] : "")));
        }

        var unrelatedSource = Path.Combine(_workspace.Path, "other", "OpenConsole.exe");
        WriteFile(unrelatedSource, "unrelated");
        var unrelatedItem = new XElement("ResolvedFileToPublish",
            new XAttribute("Include", unrelatedSource),
            new XElement("NuGetPackageId", "OtherPackage"),
            new XElement("PathInPackage", "native/OpenConsole.exe"),
            new XElement("RelativePath", "other/OpenConsole.exe"));
        var publishItems = new XElement("ItemGroup",
            new XElement("ResolvedFileToPublish",
                new XAttribute("Include", "@(_ResolvedCopyLocalPublishAssets);@(RuntimeTargetsCopyLocalItems)"),
                new XElement("RelativePath", "%(DestinationSubDirectory)%(Filename)%(Extension)")),
            unrelatedItem);
        if (duplicateUnrelatedAsset)
        {
            publishItems.Add(new XElement("ResolvedFileToPublish",
                new XAttribute("Include", Path.Combine(_workspace.Path, "duplicate", "OpenConsole.exe")),
                new XElement("RelativePath", "other/OpenConsole.exe")));
        }

        // The SDK produces flattened native items for RID builds but full runtimes/<rid>/native
        // paths for portable builds. Seed those shapes without restoring a synthetic NuGet package;
        // retain the SDK's real duplicate-output check and exercise the production target hooks.
        var project = new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("ImportDirectoryBuildProps", "false"),
                new XElement("ImportDirectoryBuildTargets", "false")),
            new XElement("Import", new XAttribute("Project", "Sdk.props"), new XAttribute("Sdk", "Microsoft.NET.Sdk")),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net11.0"),
                new XElement("RuntimeIdentifier", rid),
                new XElement("PublishSingleFile", singleFile),
                new XElement("IncludeNativeLibrariesForSelfExtract", "true")),
            new XElement("Import", new XAttribute("Project", "Sdk.targets"), new XAttribute("Sdk", "Microsoft.NET.Sdk")),
            new XElement("Import", new XAttribute("Project", Path.Combine(RepoRoot.Path, "src", "Aspire.Hosting", "buildTransitive", "Aspire.Hosting.targets"))),
            new XElement("Target", new XAttribute("Name", "ResolvePackageAssets"), nativeItems),
            new XElement("Target", new XAttribute("Name", "_ResolveCopyLocalAssetsForPublish"),
                new XAttribute("DependsOnTargets", "ResolvePackageAssets"),
                new XElement("ItemGroup", new XElement("_ResolvedCopyLocalPublishAssets",
                    new XAttribute("Include", "@(NativeCopyLocalItems)"),
                    new XElement("DestinationSubPath", "%(Filename)%(Extension)"),
                    new XElement("DestinationSubDirectory", "")))),
            new XElement("Target", new XAttribute("Name", "ComputeResolvedFilesToPublishList"),
                new XAttribute("DependsOnTargets", "_ResolveCopyLocalAssetsForPublish"), publishItems),
            new XElement("Target", new XAttribute("Name", "ComputeFilesToPublish"),
                new XAttribute("DependsOnTargets", "ComputeResolvedFilesToPublishList")),
            new XElement("Target", new XAttribute("Name", "CopyTestFiles"),
                new XAttribute("DependsOnTargets", "ComputeFilesToPublish"),
                new XElement("Copy", new XAttribute("SourceFiles", "@(ResolvedFileToPublish)"),
                    new XAttribute("DestinationFiles", "@(ResolvedFileToPublish->'$(MSBuildProjectDirectory)/publish/%(RelativePath)')")))));
        var projectPath = Path.Combine(_workspace.Path, "Publish.proj");
        project.Save(projectPath);
        return projectPath;
    }

    private static string[] GetNativePaths(string rid) => rid switch
    {
        "win-x64" => ["arm64/OpenConsole.exe", "conpty.dll", "hex1bpty.exe", "x64/OpenConsole.exe"],
        "win-arm64" => ["arm64/OpenConsole.exe", "conpty.dll", "hex1bpty.exe"],
        "" => GetNativePaths("win-x64").Select(path => $"runtimes/win-x64/native/{path}")
            .Concat(GetNativePaths("win-arm64").Select(path => $"runtimes/win-arm64/native/{path}"))
            .Append("runtimes/linux-x64/native/libhex1binterop.so").ToArray(),
        _ => ["libhex1binterop.so"]
    };

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private async Task<(int ExitCode, string Output)> RunDotNetAsync(string[] arguments, string? packages = null)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = _workspace.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (packages is not null)
        {
            startInfo.Environment["NUGET_PACKAGES"] = packages;
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await stdout + await stderr;
        _output.WriteLine(output);
        return (process.ExitCode, output);
    }
}
