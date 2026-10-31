// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class BundleBuildWrapperTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("bash", "/p:", "true")]
    [InlineData("bash", "-p:", "true")]
    [InlineData("bash", "/p:", "false")]
    [InlineData("pwsh", "/p:", "true")]
    [InlineData("pwsh", "-p:", "true")]
    [InlineData("pwsh", "/p:", "false")]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersTranslateLegacyNativeSkip(string shell, string prefix, string value)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string[] arguments = [prefix + "SkipNativeBuild=" + value];

        await ExecuteAsync(workspace, shell, arguments);

        Assert.Equal(value == "true" ? ["ARG|/p:SkipCliArtifacts=true"] : [],
            PropertyArguments(workspace, "bundle"));
        Assert.Equal(arguments.Select(argument => "ARG|" + argument), PropertyArguments(workspace, "repository"));
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("pwsh")]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersLeaveAdvancedControlsToDirectMsbuild(string shell)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string[] arguments = ["/p:SkipDashboardPublish=true", "/p:SkipCliArtifacts=true"];

        await ExecuteAsync(workspace, shell, arguments);

        Assert.Empty(PropertyArguments(workspace, "bundle"));
        Assert.Equal(arguments.Select(argument => "ARG|" + argument), PropertyArguments(workspace, "repository"));
    }

    private async Task ExecuteAsync(TemporaryWorkspace workspace, string shell, string[] arguments)
    {
        Assert.SkipWhen(shell == "bash" && OperatingSystem.IsWindows(), "The Bash wrapper requires a Unix host.");
        var root = workspace.CreateDirectory("repository with spaces").FullName;
        var common = Directory.CreateDirectory(Path.Combine(root, "eng", "common")).FullName;
        var sdk = Directory.CreateDirectory(Path.Combine(root, ".dotnet")).FullName;
        // Run complete production entry points; replace only the external build
        // driver and dotnet command with argument recorders.
        foreach (var name in new[] { "build.sh", "build.ps1" })
        {
            File.Copy(Path.Combine(RepoRoot.Path, "eng", name), Path.Combine(root, "eng", name));
        }
        await File.WriteAllTextAsync(Path.Combine(common, "build.sh"), """
            #!/usr/bin/env bash
            for arg in "$@"; do printf 'ARG|%s\n' "$arg"; done > "$TEST_ROOT/repository"
            """);
        await File.WriteAllTextAsync(Path.Combine(common, "build.ps1"), """
            [IO.File]::WriteAllLines("$env:TEST_ROOT/repository", @($args | ForEach-Object { "ARG|$_" }))
            $global:LASTEXITCODE = 0
            """);
        await File.WriteAllTextAsync(Path.Combine(sdk, "dotnet"), """
            #!/usr/bin/env bash
            for arg in "$@"; do printf 'ARG|%s\n' "$arg"; done > "$TEST_ROOT/bundle"
            """);
        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in new[] { Path.Combine(common, "build.sh"), Path.Combine(sdk, "dotnet") })
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        var launcher = Path.Combine(root, "launch.ps1");
        await File.WriteAllTextAsync(Path.Combine(root, "arguments.json"), JsonSerializer.Serialize(arguments));
        await File.WriteAllTextAsync(launcher, """
            $ErrorActionPreference = 'Stop'
            $buildArguments = @(Get-Content "$PSScriptRoot/arguments.json" -Raw | ConvertFrom-Json)
            function global:dotnet {
              [IO.File]::WriteAllLines("$env:TEST_ROOT/bundle", @($args | ForEach-Object { "ARG|$_" }))
              $global:LASTEXITCODE = 0
            }
            if ($env:TEST_SHELL -eq 'bash') {
              & bash "$PSScriptRoot/eng/build.sh" --bundle --warnAsError false @buildArguments
            } else {
              & "$PSScriptRoot/eng/build.ps1" -bundle -warnAsError false @buildArguments
            }
            exit $LASTEXITCODE
            """);
        using var command = new PowerShellCommand(launcher, output)
            .WithWorkingDirectory(root)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithEnvironmentVariable("TEST_ROOT", workspace.Path)
            .WithEnvironmentVariable("TEST_SHELL", shell)
            .WithEnvironmentVariable("CI", "false");
        var result = await command.ExecuteAsync();
        result.EnsureSuccessful();
    }

    private static IEnumerable<string> PropertyArguments(TemporaryWorkspace workspace, string name) =>
        File.ReadAllLines(Path.Combine(workspace.Path, name))
            .Where(line => line.StartsWith("ARG|/p:Skip", StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith("ARG|-p:Skip", StringComparison.OrdinalIgnoreCase));
}
