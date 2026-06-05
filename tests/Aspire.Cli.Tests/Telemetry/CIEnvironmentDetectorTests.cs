// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Telemetry;

public class CIEnvironmentDetectorTests
{
    [Fact]
    public void IsCIEnvironment_ReturnsFalse_WhenNoVariablesSet()
    {
        var config = new ConfigurationBuilder().Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.False(detector.IsCIEnvironment());
    }

    [Theory]
    [InlineData("TF_BUILD", "true")]
    [InlineData("TF_BUILD", "True")]
    [InlineData("TF_BUILD", "TRUE")]
    [InlineData("TF_BUILD", "1")]
    [InlineData("GITHUB_ACTIONS", "true")]
    [InlineData("GITHUB_ACTIONS", "1")]
    [InlineData("APPVEYOR", "true")]
    [InlineData("APPVEYOR", "1")]
    [InlineData("CI", "true")]
    [InlineData("CI", "1")]
    [InlineData("TRAVIS", "true")]
    [InlineData("TRAVIS", "1")]
    [InlineData("CIRCLECI", "true")]
    [InlineData("CIRCLECI", "1")]
    public void IsCIEnvironment_ReturnsTrue_ForBooleanVariables(string varName, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [varName] = value })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.True(detector.IsCIEnvironment());
    }

    [Theory]
    [InlineData("TF_BUILD", "false")]
    [InlineData("TF_BUILD", "0")]
    [InlineData("TF_BUILD", "")]
    [InlineData("GITHUB_ACTIONS", "false")]
    [InlineData("CI", "no")]
    public void IsCIEnvironment_ReturnsFalse_ForBooleanVariablesWithNonTrueValues(string varName, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [varName] = value })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.False(detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_ReturnsTrue_ForAwsCodeBuild()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CODEBUILD_BUILD_ID"] = "build-123",
                ["AWS_REGION"] = "us-east-1"
            })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.True(detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_ReturnsFalse_ForAwsCodeBuild_WhenOnlyBuildIdSet()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CODEBUILD_BUILD_ID"] = "build-123"
            })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.False(detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_ReturnsFalse_ForAwsCodeBuild_WhenOnlyRegionSet()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AWS_REGION"] = "us-east-1"
            })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.False(detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_ReturnsTrue_ForJenkins()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BUILD_ID"] = "42",
                ["BUILD_URL"] = "http://jenkins/job/42"
            })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.True(detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_ReturnsFalse_ForJenkins_WhenOnlyBuildIdSet()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BUILD_ID"] = "42"
            })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.False(detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_ReturnsTrue_ForGoogleCloudBuild()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BUILD_ID"] = "build-456",
                ["PROJECT_ID"] = "my-project"
            })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.True(detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_ReturnsFalse_ForGoogleCloudBuild_WhenOnlyProjectIdSet()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PROJECT_ID"] = "my-project"
            })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.False(detector.IsCIEnvironment());
    }

    [Theory]
    [InlineData("TEAMCITY_VERSION", "2023.1")]
    [InlineData("TEAMCITY_VERSION", "any-value")]
    [InlineData("JB_SPACE_API_URL", "https://space.example.com")]
    public void IsCIEnvironment_ReturnsTrue_ForPresenceOnlyVariables(string varName, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [varName] = value })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.True(detector.IsCIEnvironment());
    }

    [Theory]
    [InlineData("TEAMCITY_VERSION", "")]
    [InlineData("JB_SPACE_API_URL", "")]
    public void IsCIEnvironment_ReturnsFalse_ForPresenceOnlyVariables_WhenEmpty(string varName, string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [varName] = value })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.False(detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_ReturnsTrue_WhenMultipleCIVariablesSet()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CI"] = "true",
                ["GITHUB_ACTIONS"] = "true"
            })
            .Build();
        var detector = new CIEnvironmentDetector(config);

        Assert.True(detector.IsCIEnvironment());
    }
}
