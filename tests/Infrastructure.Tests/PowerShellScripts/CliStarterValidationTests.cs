// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class CliStarterValidationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "run")]
    [InlineData(true, "run")]
    [InlineData(false, "start")]
    [InlineData(true, "start")]
    [RequiresTools(["pwsh"])]
    public async Task ValidatesGoFrontendAndStopsWithoutDeletingDependencies(bool linux, string failedPhase)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var wrapperPath = Path.Combine(workspace.Path, "validate.ps1");
        var validationRoot = Path.Combine(workspace.Path, "validation");
        var eventsPath = Path.Combine(workspace.Path, "events.jsonl");
        await File.WriteAllTextAsync(wrapperPath, """
            $ErrorActionPreference = 'Stop'
            Set-Variable -Name IsLinux -Value ($env:TEST_LINUX -eq 'true') -Force

            function Write-Invocation($Command, $Arguments, $Directory = (Get-Location).Path)
            {
                @{
                    template = Split-Path (Split-Path $Directory) -Leaf
                    command = $Command
                    arguments = @($Arguments)
                } | ConvertTo-Json -Compress | Add-Content $env:TEST_EVENTS
            }

            function aspire
            {
                if ($args[0] -eq 'new')
                {
                    $projectRoot = $args[[Array]::IndexOf($args, '--output') + 1]
                    foreach ($relativePath in @('node_modules', 'frontend/node_modules'))
                    {
                        $dependencies = Join-Path $projectRoot $relativePath
                        New-Item -ItemType Directory -Path $dependencies -Force | Out-Null
                        Set-Content (Join-Path $dependencies 'preserved.txt') 'installed dependency'
                    }
                }
                else
                {
                    Write-Invocation $args[0] @($args | Select-Object -Skip 1)
                    if ($args[0] -eq 'logs')
                    {
                        'installer diagnostic'
                    }
                }
            }

            # Replace only external process execution; run the complete production script,
            # including its resource checks, failure aggregation, and finally cleanup.
            function Start-Process
            {
                param($FilePath, $ArgumentList, $WorkingDirectory,
                    $RedirectStandardOutput, $RedirectStandardError, [switch]$Wait, [switch]$PassThru)

                if ($FilePath -ne 'aspire')
                {
                    throw "Unexpected executable: $FilePath"
                }

                Write-Invocation $ArgumentList[0] @($ArgumentList | Select-Object -Skip 1) $WorkingDirectory
                if ($ArgumentList[0] -in @('run', 'start'))
                {
                    $global:TestPhase = $ArgumentList[0]
                }
                $exitCode = 0
                if ($env:TEST_FAIL_PHASE -eq $global:TestPhase -and
                    $WorkingDirectory -like '*AspireCliGoStarterSmoke' -and
                    $ArgumentList[0] -eq 'wait' -and $ArgumentList[1] -eq 'frontend')
                {
                    $exitCode = 1
                }

                $stdout = if ($ArgumentList[0] -in @('run', 'start'))
                {
                    @{ appHostPid = 123; appHostPath = $WorkingDirectory; logFile = 'apphost.log' } |
                        ConvertTo-Json -Compress
                }
                else
                {
                    "Resource command exited with code $exitCode"
                }
                Set-Content $RedirectStandardOutput $stdout
                Set-Content $RedirectStandardError ''
                $process = [pscustomobject]@{ Id = 123; ExitCode = $exitCode }
                $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($Timeout) return $true }
                return $process
            }

            & $env:TEST_SCRIPT -PRNumber 1 -MaxStartupSeconds 1 `
                -ResourceReadyTimeoutSeconds 1 -ValidationRoot $env:TEST_ROOT
            """);

        using var command = new PowerShellCommand(wrapperPath, output)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("TEST_SCRIPT", Path.Combine(RepoRoot.Path, "eng", "scripts", "cli-starter-validation.ps1"))
            .WithEnvironmentVariable("TEST_ROOT", validationRoot)
            .WithEnvironmentVariable("TEST_EVENTS", eventsPath)
            .WithEnvironmentVariable("TEST_LINUX", linux ? "true" : "false")
            .WithEnvironmentVariable("TEST_FAIL_PHASE", failedPhase);

        var result = await command.ExecuteAsync();
        if (failedPhase != "none")
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("aspire-go-starter: aspire wait for resource frontend failed with exit code 1.", result.Output);
        }
        else
        {
            result.EnsureSuccessful();
        }

        var invocations = File.ReadAllLines(eventsPath)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(item => item.GetProperty("template").GetString() == "aspire-go-starter")
            .ToArray();
        var waitedResources = invocations
            .Where(item => item.GetProperty("command").GetString() == "wait")
            .Select(item => item.GetProperty("arguments")[0].GetString())
            .ToArray();
        var commands = invocations.Select(item => item.GetProperty("command").GetString()).ToArray();
        if (failedPhase == "run")
        {
            Assert.Equal(["frontend"], waitedResources);
            Assert.Equal(["add", "run", "wait", "logs", "stop"], commands);
        }
        else
        {
            Assert.Equal(linux && failedPhase == "none" ? ["frontend", "app", "frontend", "cache"] : ["frontend", "app", "frontend"], waitedResources);
            var finalCommands = failedPhase == "start" ? new[] { "logs", "stop" } : ["stop"];
            Assert.Equal(["add", "run", "wait", "stop", "start", "resources", .. waitedResources.Skip(1).Select(_ => "wait"), .. finalCommands], commands);
        }

        var goProject = Path.Combine(validationRoot, "aspire-go-starter", "AspireCliGoStarterSmoke");
        if (failedPhase != "none")
        {
            var logInvocation = Assert.Single(invocations, item => item.GetProperty("command").GetString() == "logs");
            Assert.Contains("--include-hidden", logInvocation.GetProperty("arguments").EnumerateArray().Select(arg => arg.GetString()));
            var logFile = failedPhase == "run" ? "aspire-run-resource-logs.log" : "aspire-resource-logs.log";
            var logPath = Path.Combine(validationRoot, "aspire-go-starter", "diagnostics", logFile);
            Assert.Equal("installer diagnostic", File.ReadAllText(logPath).Trim());
        }

        foreach (var relativePath in new[] { "node_modules", Path.Combine("frontend", "node_modules") })
        {
            Assert.Equal("installed dependency", File.ReadAllText(Path.Combine(goProject, relativePath, "preserved.txt")).Trim());
        }
    }
}
