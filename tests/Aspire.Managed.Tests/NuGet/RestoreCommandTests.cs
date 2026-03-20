// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Managed.NuGet.Commands;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace Aspire.Managed.Tests.NuGet;

public class RestoreCommandTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public void RestoreCommand_RespectsNuGetConfigGlobalPackagesFolder()
    {
        var customPackagesDir = Path.GetFullPath(Path.Combine(_tempDir.Path, "custom-packages"));
        var nugetConfigPath = Path.Combine(_tempDir.Path, "NuGet.config");

        File.WriteAllText(nugetConfigPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{customPackagesDir}" />
              </config>
            </configuration>
            """);

        // Run in a separate process so NUGET_PACKAGES env var from the parent
        // doesn't interfere. The env var takes precedence over config files
        // in NuGet's resolution order.
        var options = new RemoteInvokeOptions();
        options.StartInfo.RedirectStandardOutput = true;
        options.StartInfo.Environment.Remove("NUGET_PACKAGES");

        using var result = RemoteExecutor.Invoke(static async (tempDirPath) =>
        {
            var command = RestoreCommand.Create();
            var outputDir = Path.Combine(tempDirPath, "obj");

            await command.Parse(["--package", "Fake.Package,1.0.0", "--no-nuget-org", "--verbose", "--output", outputDir, "--working-dir", tempDirPath]).InvokeAsync();
        }, _tempDir.Path, options);

        var consoleOutput = result.Process.StandardOutput.ReadToEnd();
        Assert.Contains($"Packages: {customPackagesDir}", consoleOutput);
    }

    [Fact]
    public void RestoreCommand_RespectsNuGetPackagesEnvironmentVariable()
    {
        var customPackagesDir = Path.GetFullPath(Path.Combine(_tempDir.Path, "env-packages"));

        // Run in a separate process with NUGET_PACKAGES set to the custom directory.
        // The env var takes priority over all config file settings.
        var options = new RemoteInvokeOptions();
        options.StartInfo.RedirectStandardOutput = true;
        options.StartInfo.Environment["NUGET_PACKAGES"] = customPackagesDir;

        using var result = RemoteExecutor.Invoke(static async (tempDirPath) =>
        {
            var command = RestoreCommand.Create();
            var outputDir = Path.Combine(tempDirPath, "obj");

            await command.Parse(["--package", "Fake.Package,1.0.0", "--no-nuget-org", "--verbose", "--output", outputDir, "--working-dir", tempDirPath]).InvokeAsync();
        }, _tempDir.Path, options);

        var consoleOutput = result.Process.StandardOutput.ReadToEnd();
        Assert.Contains($"Packages: {customPackagesDir}", consoleOutput);
    }
}
