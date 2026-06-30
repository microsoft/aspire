// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json;
using Aspire.Managed.NuGet.Commands;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace Aspire.Managed.Tests.NuGet;

public class SearchCommandTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void SearchCommand_AcceptsExactMatchWithBoundedTimeout()
    {
        RemoteExecutor.Invoke(static async (tempDirPath) =>
        {
            var packageSourcePath = Path.Combine(tempDirPath, "packages");
            Directory.CreateDirectory(packageSourcePath);

            var command = SearchCommand.Create();
            await using var outputWriter = new StringWriter();
            var oldOutput = Console.Out;
            Console.SetOut(outputWriter);

            try
            {
                var exitCode = await command.Parse([
                    "--query", "Aspire.Cli",
                    "--exact-match",
                    "--timeout-seconds", "1",
                    "--source", packageSourcePath,
                    "--format", "json"]).InvokeAsync();

                Assert.Equal(0, exitCode);
            }
            finally
            {
                Console.SetOut(oldOutput);
            }

            Assert.Contains("\"totalHits\":0", outputWriter.ToString());
        }, _tempDir.Path).Dispose();
    }

    [Fact]
    public void SearchCommand_ExactMatchMergesVersionsAcrossSources()
    {
        RemoteExecutor.Invoke(static async (tempDirPath) =>
        {
            var firstSourcePath = Path.Combine(tempDirPath, "source1");
            var secondSourcePath = Path.Combine(tempDirPath, "source2");
            Directory.CreateDirectory(firstSourcePath);
            Directory.CreateDirectory(secondSourcePath);
            CreatePackage(firstSourcePath, "Aspire.Cli", "13.3.0");
            CreatePackage(secondSourcePath, "Aspire.Cli", "13.4.0");

            var command = SearchCommand.Create();
            await using var outputWriter = new StringWriter();
            var oldOutput = Console.Out;
            Console.SetOut(outputWriter);

            try
            {
                var exitCode = await command.Parse([
                    "--query", "Aspire.Cli",
                    "--exact-match",
                    "--timeout-seconds", "5",
                    "--source", firstSourcePath,
                    "--source", secondSourcePath,
                    "--format", "json"]).InvokeAsync();

                Assert.Equal(0, exitCode);
            }
            finally
            {
                Console.SetOut(oldOutput);
            }

            using var document = JsonDocument.Parse(outputWriter.ToString());
            var package = document.RootElement.GetProperty("packages").EnumerateArray().Single();
            Assert.Equal("13.4.0", package.GetProperty("version").GetString());
            Assert.Equal(
                ["13.3.0", "13.4.0"],
                package.GetProperty("allVersions").EnumerateArray().Select(version => version.GetString()!).ToArray());
        }, _tempDir.Path).Dispose();
    }

    [Fact]
    public void SearchCommand_CallerCancellationReturnsFailure()
    {
        RemoteExecutor.Invoke(static async (tempDirPath) =>
        {
            var packageSourcePath = Path.Combine(tempDirPath, "packages");
            Directory.CreateDirectory(packageSourcePath);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var executeSearchAsync = typeof(SearchCommand).GetMethod("ExecuteSearchAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(executeSearchAsync);

            var task = (Task<int>)executeSearchAsync.Invoke(null, [
                "Aspire.Cli",
                false,
                100,
                0,
                true,
                30,
                new[] { packageSourcePath },
                null,
                tempDirPath,
                "json",
                false,
                cts.Token])!;
            var exitCode = await task;

            Assert.NotEqual(0, exitCode);
        }, _tempDir.Path).Dispose();
    }

    private static void CreatePackage(string packagesDirectory, string packageId, string version)
    {
        var packagePath = Path.Combine(packagesDirectory, $"{packageId}.{version}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry($"{packageId}.nuspec");
        using var writer = new StreamWriter(entry.Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{packageId}</id>
                <version>{version}</version>
                <authors>Aspire</authors>
                <description>Test package</description>
              </metadata>
            </package>
            """);
    }
}
