// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Regression coverage for https://github.com/microsoft/aspire/issues/19992: parallel pwsh
/// processes launched by these tests must not rewrite the shared multicore-JIT startup profile.
/// </summary>
public class PowerShellStartupProfileTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;

    public PowerShellStartupProfileTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void TestProcessDisablesStartupProfileGatheringForChildProcesses()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable(PowerShellProfileOptimizationDefaults.NoProfileGatherVariable));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    [SkipOnPlatform(TestPlatforms.Windows, "On Windows pwsh resolves its cache via the LocalApplicationData known folder, which can't be redirected per process.")]
    public async Task PwshLaunchedByTestsDoesNotWriteSharedStartupProfile()
    {
        // pwsh stores StartupProfileData-* under $XDG_CACHE_HOME/powershell on Unix.
        var cacheRoot = Path.Combine(_workspace.Path, "cache");
        Directory.CreateDirectory(cacheRoot);

        var scriptPath = Path.Combine(_workspace.Path, "noop.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");

        using var cmd = new PowerShellCommand(scriptPath, _output)
            .WithEnvironmentVariable("XDG_CACHE_HOME", cacheRoot)
            .WithTimeout(TimeSpan.FromMinutes(2));

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("ok", result.Output);

        var profiles = Directory.GetFiles(cacheRoot, "StartupProfileData-*", SearchOption.AllDirectories);
        Assert.True(
            profiles.Length == 0,
            $"pwsh wrote a shared multicore-JIT startup profile, which concurrent pwsh processes can read while it is being rewritten: {string.Join(", ", profiles)}");
    }
}
