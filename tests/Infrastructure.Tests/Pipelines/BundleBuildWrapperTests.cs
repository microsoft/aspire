// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class BundleBuildWrapperTests(ITestOutputHelper output)
{
    private static readonly string[] s_controls =
    [
        "SkipAspireManagedPublish", "SkipDashboardPublish", "SkipTerminalHostPublish",
        "SkipAppHostServerPublish", "SkipTrayPublish", "SkipCliArtifacts"
    ];

    private static readonly string[] s_retired =
        ["SkipNativeBuild", "SkipManagedBuild", "SkipAppHostServerBuild", "SkipTrayBuild"];

    [Theory]
    [InlineData("bash", "/p:")]
    [InlineData("bash", "-p:")]
    [InlineData("pwsh", "/p:")]
    [InlineData("pwsh", "-p:")]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersForwardPublishControls(string shell, string prefix)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var arguments = s_controls.Select((name, index) => $"{prefix}{name}={(index % 2 == 0 ? "true" : "false")}").ToArray();

        await ExecuteAsync(workspace, shell, arguments, inheritedSkips: false);

        Assert.Equal(arguments.Select(argument => "ARG|" + argument), Read(workspace, "bundle")
            .Where(line => s_controls.Any(name => line.Contains(name, StringComparison.Ordinal))));
        Assert.Equal(arguments.Select(argument => "ARG|" + argument), Read(workspace, "repository")
            .Where(line => s_controls.Any(name => line.Contains(name, StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData("bash", "/p:")]
    [InlineData("bash", "-p:")]
    [InlineData("pwsh", "/p:")]
    [InlineData("pwsh", "-p:")]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersPreferExplicitCliSelection(string shell, string prefix)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string[] arguments = [prefix + "SkipNativeBuild=true", prefix + "SkipCliArtifacts=false"];

        await ExecuteAsync(workspace, shell, arguments, inheritedSkips: true);

        Assert.Equal(["ARG|" + prefix + "SkipCliArtifacts=false"], PropertyArguments(Read(workspace, "bundle")));
        Assert.Equal(arguments.Select(argument => "ARG|" + argument), PropertyArguments(Read(workspace, "repository")));
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("pwsh")]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersDoNotLeakRepositorySkipEnvironment(string shell)
    {
        using var workspace = TemporaryWorkspace.Create(output);

        await ExecuteAsync(workspace, shell, [], inheritedSkips: true);

        Assert.Equal([], Read(workspace, "bundle").Where(line => line.StartsWith("ENV|", StringComparison.Ordinal)));
        var expected = s_retired.Select(name => "ENV|" + name + "=true");
        Assert.Equal(expected, Read(workspace, "repository").Where(line => line.StartsWith("ENV|", StringComparison.Ordinal)));
        Assert.Equal(expected, Read(workspace, "after"));
    }

    [Theory]
    [InlineData("bash", "/p:")]
    [InlineData("bash", "-p:")]
    [InlineData("bash", "environment")]
    [InlineData("pwsh", "/p:")]
    [InlineData("pwsh", "-p:")]
    [InlineData("pwsh", "environment")]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersHonorRepositoryNativeSkipAtBundleBoundary(string shell, string source)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string[] arguments = source == "environment" ? [] : [source + "SkipNativeBuild=true"];

        await ExecuteAsync(workspace, shell, arguments, inheritedSkips: source == "environment");

        Assert.Equal(["ARG|/p:SkipCliArtifacts=true"], PropertyArguments(Read(workspace, "bundle")));
        Assert.Equal(arguments.Select(argument => "ARG|" + argument), Read(workspace, "repository")
            .Where(line => line.StartsWith("ARG|", StringComparison.Ordinal) && line.Contains("SkipNative", StringComparison.Ordinal)));
    }

    public static TheoryData<string, string, string, bool, bool> CompoundPropertyCases
    {
        get
        {
            var cases = new TheoryData<string, string, string, bool, bool>();
            foreach (var shell in new[] { "bash", "pwsh" })
            {
                foreach (var prefix in new[] { "/p:", "-p:" })
                {
                    foreach (var separator in new[] { ";", "," })
                    {
                        foreach (var reverse in new[] { false, true })
                        {
                            foreach (var explicitCli in new[] { false, true })
                            {
                                cases.Add(shell, prefix, separator, reverse, explicitCli);
                            }
                        }
                    }
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(CompoundPropertyCases))]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersSplitCompoundPropertiesOnlyAtBundleBoundary(
        string shell, string prefix, string separator, bool reverse, bool explicitCli)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        const string managed = "sKiPaSpIrEmAnAgEdPuBlIsH=true";
        const string cli = "sKiPcLiArTiFaCtS=false";
        var assignments = new List<string>
        {
            managed, "sKiPnAtIvEbUiLd=true", "SkipManagedBuild=true",
            "SkipAppHostServerBuild=false", "SkipTrayBuild=true", "RepositoryOnly=original"
        };
        if (explicitCli)
        {
            assignments.Add(cli);
        }
        if (reverse)
        {
            assignments.Reverse();
        }
        var argument = prefix + string.Join(separator, assignments);

        await ExecuteAsync(workspace, shell, [argument], inheritedSkips: false);

        string[] forwarded = explicitCli
            ? reverse ? [prefix + cli, prefix + managed] : [prefix + managed, prefix + cli]
            : [prefix + managed, "/p:SkipCliArtifacts=true"];
        Assert.Equal(forwarded.Select(value => "ARG|" + value), PropertyArguments(Read(workspace, "bundle")));
        Assert.Equal(["ARG|" + argument], PropertyArguments(Read(workspace, "repository")));
    }

    public static TheoryData<string, bool, string> SkipEnvironmentCases
    {
        get
        {
            var cases = new TheoryData<string, bool, string>();
            foreach (var shell in new[] { "bash", "pwsh" })
            {
                foreach (var uppercase in new[] { false, true })
                {
                    foreach (var prefix in new[] { "", "/p:", "-p:" })
                    {
                        cases.Add(shell, uppercase, prefix);
                    }
                }
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(SkipEnvironmentCases))]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersIsolateCaseInsensitiveEnvironmentProperties(string shell, bool uppercase, string prefix)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        string[] names = ["sKiPnAtIvEbUiLd", "sKiPmAnAgEdBuIlD", "sKiPaPpHoStSeRvErBuIlD", "sKiPtRaYbUiLd"];
        if (uppercase)
        {
            names = names.Select(name => name.ToUpperInvariant()).ToArray();
        }
        var environment = new Dictionary<string, string>
        {
            [names[0]] = "true", [names[1]] = "false", [names[2]] = "True", [names[3]] = "false",
            ["WRAPPER_UNRELATED"] = "keep spaces=and;delimiters"
        };
        string[] arguments = prefix.Length == 0 ? [] : [prefix + "sKiPcLiArTiFaCtS=false"];

        await ExecuteAsync(workspace, shell, arguments, environment);

        Assert.Equal(["ENV|WRAPPER_UNRELATED=keep spaces=and;delimiters"], Read(workspace, "bundle")
            .Where(line => line.StartsWith("ENV|", StringComparison.Ordinal)));
        Assert.Equal(["ARG|" + (prefix.Length == 0 ? "/p:SkipCliArtifacts=true" : arguments[0])],
            PropertyArguments(Read(workspace, "bundle")));
        var expectedEnvironment = environment.Select(pair => $"ENV|{pair.Key}={pair.Value}").Order(StringComparer.Ordinal);
        Assert.Equal(expectedEnvironment, Read(workspace, "repository")
            .Where(line => line.StartsWith("ENV|", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(expectedEnvironment, Read(workspace, "after").Order(StringComparer.Ordinal));
        Assert.Equal(arguments.Select(argument => "ARG|" + argument), PropertyArguments(Read(workspace, "repository")));
    }

    [Theory]
    [InlineData("bash", false, false)]
    [InlineData("bash", false, true)]
    [InlineData("bash", true, false)]
    [InlineData("bash", true, true)]
    [InlineData("pwsh", false, false)]
    [InlineData("pwsh", false, true)]
    [InlineData("pwsh", true, false)]
    [InlineData("pwsh", true, true)]
    [RequiresTools(["pwsh", "bash"])]
    public async Task BundleWrappersPreferNewCliEnvironmentOverLegacyFallback(string shell, bool uppercase, bool explicitCli)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var legacy = uppercase ? "SKIPNATIVEBUILD" : "SkipNativeBuild";
        var current = uppercase ? "SKIPCLIARTIFACTS" : "sKiPcLiArTiFaCtS";
        var environment = new Dictionary<string, string>
        {
            [legacy] = "true", [current] = "false"
        };
        string[] arguments = explicitCli ? ["-p:sKiPcLiArTiFaCtS=true"] : [];

        await ExecuteAsync(workspace, shell, arguments, environment);

        Assert.Equal(arguments.Select(argument => "ARG|" + argument), PropertyArguments(Read(workspace, "bundle")));
        Assert.Equal(["ENV|" + current + "=false"], Read(workspace, "bundle")
            .Where(line => line.StartsWith("ENV|", StringComparison.Ordinal)));
        var expected = environment.Select(pair => $"ENV|{pair.Key}={pair.Value}").Order(StringComparer.Ordinal);
        Assert.Equal(expected, Read(workspace, "repository")
            .Where(line => line.StartsWith("ENV|", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(expected, Read(workspace, "after").Order(StringComparer.Ordinal));
        Assert.Equal(arguments.Select(argument => "ARG|" + argument), PropertyArguments(Read(workspace, "repository")));
    }

    private static IEnumerable<string> PropertyArguments(string[] lines) => lines.Where(line =>
        line.StartsWith("ARG|/p:Skip", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("ARG|-p:Skip", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("RepositoryOnly=original", StringComparison.Ordinal));

    private Task ExecuteAsync(TemporaryWorkspace workspace, string shell, string[] arguments, bool inheritedSkips) =>
        ExecuteAsync(workspace, shell, arguments, s_retired.ToDictionary(name => name, _ => inheritedSkips ? "true" : ""));

    private async Task ExecuteAsync(TemporaryWorkspace workspace, string shell, string[] arguments,
        IReadOnlyDictionary<string, string> skipEnvironment)
    {
        Assert.SkipWhen(shell == "bash" && OperatingSystem.IsWindows(), "The Bash wrapper requires a Unix host.");
        var root = workspace.CreateDirectory("repository with spaces").FullName;
        var common = Directory.CreateDirectory(Path.Combine(root, "eng", "common")).FullName;
        var sdk = Directory.CreateDirectory(Path.Combine(root, ".dotnet")).FullName;
        // Copy the complete production entry points; only their external build
        // toolset and dotnet command are replaced with argument/environment recorders.
        foreach (var name in new[] { "build.sh", "build.ps1" })
        {
            File.Copy(Path.Combine(RepoRoot.Path, "eng", name), Path.Combine(root, "eng", name));
        }
        await File.WriteAllTextAsync(Path.Combine(common, "build.sh"), """
            #!/usr/bin/env bash
            for arg in "$@"; do printf 'ARG|%s\n' "$arg"; done > "$TEST_ROOT/repository"
            IFS='|' read -r -a names <<< "$TEST_ENV_NAMES"
            for name in "${names[@]}"; do
              if printenv "$name" >/dev/null; then printf 'ENV|%s=%s\n' "$name" "$(printenv "$name")"; fi
            done >> "$TEST_ROOT/repository"
            """);
        await File.WriteAllTextAsync(Path.Combine(common, "build.ps1"), """
            [IO.File]::WriteAllLines("$env:TEST_ROOT/repository", @($args | ForEach-Object { "ARG|$_" }))
            foreach ($name in $env:TEST_ENV_NAMES.Split('|')) {
              $value = [Environment]::GetEnvironmentVariable($name)
              if ($null -ne $value) { Add-Content "$env:TEST_ROOT/repository" "ENV|$name=$value" }
            }
            $global:LASTEXITCODE = 0
            """);
        await File.WriteAllTextAsync(Path.Combine(sdk, "dotnet"), """
            #!/usr/bin/env bash
            for arg in "$@"; do printf 'ARG|%s\n' "$arg"; done > "$TEST_ROOT/bundle"
            IFS='|' read -r -a names <<< "$TEST_ENV_NAMES"
            for name in "${names[@]}"; do
              if printenv "$name" >/dev/null; then printf 'ENV|%s=%s\n' "$name" "$(printenv "$name")"; fi
            done >> "$TEST_ROOT/bundle"
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
            $BuildArguments = @(Get-Content "$PSScriptRoot/arguments.json" -Raw | ConvertFrom-Json)
            function global:dotnet {
              [IO.File]::WriteAllLines("$env:TEST_ROOT/bundle", @($args | ForEach-Object { "ARG|$_" }))
              foreach ($name in $env:TEST_ENV_NAMES.Split('|')) {
                $value = [Environment]::GetEnvironmentVariable($name)
                if ($null -ne $value) { Add-Content "$env:TEST_ROOT/bundle" "ENV|$name=$value" }
              }
              $global:LASTEXITCODE = 0
            }
            if ($env:TEST_SHELL -eq 'bash') {
              & bash "$PSScriptRoot/eng/build.sh" --bundle --warnAsError false @BuildArguments
            } else {
              & "$PSScriptRoot/eng/build.ps1" -bundle -warnAsError false @BuildArguments
            }
            $code = $LASTEXITCODE
            [IO.File]::WriteAllLines("$env:TEST_ROOT/after", @(
              foreach ($name in $env:TEST_ENV_NAMES.Split('|')) {
                $value = [Environment]::GetEnvironmentVariable($name)
                if ($null -ne $value) { "ENV|$name=$value" }
              }
            ))
            exit $code
            """);
        using var command = new PowerShellCommand(launcher, output)
            .WithWorkingDirectory(root)
            .WithTimeout(TimeSpan.FromMinutes(1))
            .WithEnvironmentVariable("TEST_ROOT", workspace.Path)
            .WithEnvironmentVariable("TEST_SHELL", shell)
            .WithEnvironmentVariable("TEST_ENV_NAMES", string.Join('|', skipEnvironment.Keys))
            .WithEnvironmentVariable("CI", "false");
        foreach (var (name, value) in skipEnvironment)
        {
            command.WithEnvironmentVariable(name, value);
        }
        var result = await command.ExecuteAsync();
        result.EnsureSuccessful();
    }

    private static string[] Read(TemporaryWorkspace workspace, string name) =>
        File.ReadAllLines(Path.Combine(workspace.Path, name));
}
