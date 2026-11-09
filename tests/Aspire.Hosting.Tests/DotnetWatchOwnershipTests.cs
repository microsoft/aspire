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
                DotnetWatchOwnership.ResourceWatchSuppressionEnvironmentVariable
            ],
            DotnetWatchOwnership.PrivateEnvironmentVariables);
        Assert.All(DotnetWatchOwnership.PrivateEnvironmentVariables, name =>
            Assert.True(DotnetWatchOwnership.IsPrivateEnvironmentVariable(name.ToLowerInvariant())));
        Assert.False(DotnetWatchOwnership.IsPrivateEnvironmentVariable("APPLICATION_SETTING"));
    }
}
