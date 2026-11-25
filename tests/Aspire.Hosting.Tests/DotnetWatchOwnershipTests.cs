// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.Dcp;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class DotnetWatchOwnershipTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("ide", false)]
    [InlineData("CLI", false)]
    [InlineData("cli", true)]
    public void CliOwnershipRequiresTheExplicitOwnerValue(string? owner, bool expected)
    {
        Assert.Equal(expected, DotnetWatchOwnership.IsCliOwned(owner));
    }

    [Fact]
    public void AppHostEnvironmentRecordRestoresRecordedValuesAndUnsetsTheRest()
    {
        var original = new Dictionary<string, string?> { ["MSBuildSDKsPath"] = "/custom \"sdks\"/Sdks", ["Unrelated"] = "kept" };
        var encoded = DotnetWatchOwnership.EncodeAppHostEnvironment(name => original.GetValueOrDefault(name));
        Assert.Equal("{\"MSBuildSDKsPath\":\"/custom \\u0022sdks\\u0022/Sdks\"}", encoded);

        // As inherited from the watcher host, which loaded MSBuild from the AppHost's SDK.
        var current = new Dictionary<string, string?>
        {
            ["MSBUILD_EXE_PATH"] = "/sdk/11.0.100/MSBuild.dll",
            ["MSBuildExtensionsPath"] = "/sdk/11.0.100",
            ["MSBuildSDKsPath"] = "/sdk/11.0.100/Sdks",
            ["Unrelated"] = "kept"
        };
        DotnetWatchOwnership.RestoreAppHostEnvironment(encoded, (name, value) => current[name] = value);

        Assert.Equal(new Dictionary<string, string?>
        {
            ["MSBUILD_EXE_PATH"] = null,
            ["MSBuildExtensionsPath"] = null,
            ["MSBuildSDKsPath"] = "/custom \"sdks\"/Sdks",
            ["Unrelated"] = "kept"
        }, current);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"MSBUILD_EXE_PATH\":\"/custom/MSBuild.dll\",\"MSBuildSDKsPath\":1}")]
    [InlineData("not json")]
    public void MalformedAppHostEnvironmentRecordIsRejectedWithoutRestoringAnything(string encoded)
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => DotnetWatchOwnership.RestoreAppHostEnvironment(encoded, (_, _) => Assert.Fail("Nothing may be restored.")));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("ide", true)]
    [InlineData("cli", false)]
    public void CliOwnedResourcesDoNotDelegateToTheUpstreamExecutionService(string? owner, bool expectedIdeLaunch)
    {
        var resource = new ExecutableResource("app", "dotnet", ".");
        resource.Annotations.Add(SupportsDebuggingAnnotation.Create(
            resource.Name,
            KnownLaunchConfigurationTypes.Project,
            static _ => Task.FromResult(new ProjectLaunchConfiguration { ProjectPath = "app.csproj" })));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DotnetWatchOwnership.OwnerEnvironmentVariable] = owner,
                [KnownConfigNames.DebugSessionPort] = "localhost:12345",
                [KnownConfigNames.DebugSessionRunMode] = ExecutableLaunchMode.NoDebug
            })
            .Build();

        Assert.Equal(expectedIdeLaunch, resource.SupportsDebugging(configuration, out _));
        Assert.Equal(expectedIdeLaunch ? ExecutableLaunchMechanism.Ide : ExecutableLaunchMechanism.Process,
            new ExecutableLaunchPolicy(configuration).Decide(resource).Mechanism);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("cli", false)]
    public void CliOwnerAlsoFencesTheLegacyProjectCompatibilityPath(string? owner, bool expectedIde)
    {
#pragma warning disable CS0618
        var resource = new ProjectResource("legacy-project");
#pragma warning restore CS0618
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DotnetWatchOwnership.OwnerEnvironmentVariable] = owner,
                [KnownConfigNames.DebugSessionPort] = "localhost:12345"
            })
            .Build();

        Assert.Equal(expectedIde ? ExecutableLaunchMechanism.Ide : ExecutableLaunchMechanism.Process,
            new ExecutableLaunchPolicy(configuration).Decide(resource).Mechanism);
    }

    [Fact]
    public void PrivateEnvironmentInventoryContainsAllAppHostGenerationState()
    {
        Assert.Equal(
            [
                DotnetWatchOwnership.OwnerEnvironmentVariable,
                DotnetWatchOwnership.AppHostControlEnvironmentVariable,
                DotnetWatchOwnership.AppHostSessionEnvironmentVariable,
                DotnetWatchOwnership.ResourceWatchSuppressionEnvironmentVariable,
                DotnetWatchOwnership.AppHostEnvironmentVariable
            ],
            DotnetWatchOwnership.PrivateEnvironmentVariables);
        Assert.All(DotnetWatchOwnership.PrivateEnvironmentVariables, name =>
            Assert.True(DotnetWatchOwnership.IsPrivateEnvironmentVariable(name.ToLowerInvariant())));
        Assert.False(DotnetWatchOwnership.IsPrivateEnvironmentVariable("APPLICATION_SETTING"));
    }
}
