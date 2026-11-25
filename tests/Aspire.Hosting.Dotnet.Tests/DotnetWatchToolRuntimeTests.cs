// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using static Aspire.Hosting.Dotnet.Watch.DotnetWatchToolResolver;

namespace Aspire.Hosting.Dotnet.Tests;

// The shipped watch tool runs on a specific runtime patch (its SDK band's); older runtimes of the same major can't start it.
public class DotnetWatchToolRuntimeTests(ITestOutputHelper outputHelper)
{
    private const string ListRuntimes = """
        Microsoft.AspNetCore.App 10.0.2 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]
        Microsoft.NETCore.App 10.0.2 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
        Microsoft.NETCore.App 11.0.0-rc.2.26504.105 [/usr/share/dotnet/shared/Microsoft.NETCore.App]
        """;

    [Fact]
    public void ParsesInstalledRuntimes()
    {
        Assert.Equal(
            [new("Microsoft.AspNetCore.App", "10.0.2"), new("Microsoft.NETCore.App", "10.0.2"), new("Microsoft.NETCore.App", "11.0.0-rc.2.26504.105")],
            ParseInstalledFrameworks(ListRuntimes.Replace("\n", "\r\n")));
    }

    [Theory]
    [InlineData("10.0.14", "Minor", false)]
    [InlineData("10.0.14", "LatestMajor", true)]
    [InlineData("10.0.2", "Minor", true)]
    [InlineData("10.0.1", "Disable", false)]
    [InlineData("10.0.1", "LatestPatch", true)]
    [InlineData("9.0.10", "Minor", false)]
    [InlineData("9.0.10", "Major", true)]
    [InlineData("11.0.0-rc.2.26504.105", "Disable", true)]
    [InlineData("11.0.0-rc.1.26400.1", "LatestPatch", true)]
    [InlineData("11.0.0", "Minor", false)]
    public void RuntimeMatchesUnderRollForward(string required, string rollForward, bool available)
    {
        var requirements = new RuntimeRequirements([new("Microsoft.NETCore.App", required)], rollForward);

        var missing = FindMissingFrameworks(requirements, ParseInstalledFrameworks(ListRuntimes));

        Assert.Equal(available, missing.Count == 0);
    }

    [Fact]
    public void EveryFrameworkMustBePresent()
    {
        var requirements = new RuntimeRequirements(
            [new("Microsoft.NETCore.App", "10.0.2"), new("Microsoft.AspNetCore.App", "10.0.2"), new("Microsoft.WindowsDesktop.App", "10.0.0")],
            "Minor");

        var missing = FindMissingFrameworks(requirements, ParseInstalledFrameworks(ListRuntimes));

        Assert.Equal([new("Microsoft.WindowsDesktop.App", "10.0.0")], missing);
    }

    [Fact]
    public void ReadsFrameworksAndTheMostPermissiveRollForward()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dll = Path.Combine(workspace.Path, ToolDllName);
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), """
            {
              // comments are allowed
              "runtimeOptions": {
                "tfm": "net10.0",
                "rollForward": "LatestPatch",
                "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "10.0.14" },
                  { "name": "Microsoft.AspNetCore.App", "version": "10.0.14" }
                ]
              }
            }
            """);

        var requirements = ReadRuntimeRequirements(dll, rollForwardOverride: null);
        Assert.Equal([new("Microsoft.NETCore.App", "10.0.14"), new("Microsoft.AspNetCore.App", "10.0.14")], requirements.Frameworks);
        Assert.Equal("LatestPatch", requirements.RollForward);

        Assert.Equal("Major", ReadRuntimeRequirements(dll, "Major").RollForward);
        Assert.Equal("LatestPatch", ReadRuntimeRequirements(dll, "Disable").RollForward);
    }

    [Fact]
    public void SingleFrameworkDefaultsToMinor()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dll = Path.Combine(workspace.Path, ToolDllName);
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), """
            { "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.14" } } }
            """);

        var requirements = ReadRuntimeRequirements(dll, rollForwardOverride: null);

        Assert.Equal([new("Microsoft.NETCore.App", "10.0.14")], requirements.Frameworks);
        Assert.Equal("Minor", requirements.RollForward);
    }

    [Fact]
    public void MissingRuntimeConfigFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var ex = Assert.Throws<DistributedApplicationException>(() => ReadRuntimeRequirements(Path.Combine(workspace.Path, ToolDllName), null));

        Assert.Contains("runtimeconfig.json", ex.Message);
        Assert.Contains("without --watch", ex.Message);
    }

    [Fact]
    public async Task MissingRuntimeFailsWithAnActionableError()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dll = Path.Combine(workspace.Path, ToolDllName);
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), """
            { "runtimeOptions": { "rollForward": "Disable", "framework": { "name": "Microsoft.NETCore.App", "version": "99.0.0" } } }
            """);
        var muxer = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath ? hostPath : "dotnet";

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => EnsureRuntimeAvailableAsync(dll, muxer, "10.0.403", CancellationToken.None));

        Assert.Contains("Microsoft.NETCore.App 99.0.0", ex.Message);
        Assert.Contains("SDK 10.0.403", ex.Message);
        Assert.Contains("without --watch", ex.Message);
    }
}
