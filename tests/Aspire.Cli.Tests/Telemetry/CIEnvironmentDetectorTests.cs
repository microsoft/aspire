// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Telemetry;

public class CIEnvironmentDetectorTests
{
    [Fact]
    public void IsCIEnvironment_ReturnsFalse_WhenNoVariablesSet()
    {
        var detector = new CIEnvironmentDetector(new TestEnvironment());

        Assert.False(detector.IsCIEnvironment());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("YeS", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("On", true)]
    [InlineData("ON", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    [InlineData("2", false)]
    [InlineData("-1", false)]
    [InlineData("01", false)]
    [InlineData("+1", false)]
    [InlineData(" true ", false)]
    [InlineData(" 1 ", false)]
    [InlineData(" yes ", false)]
    [InlineData(" on ", false)]
    [InlineData("\ttrue\n", false)]
    [InlineData(" ", false)]
    [InlineData("invalid", false)]
    public void IsCIEnvironment_MatchesDotNetBooleanValues(string? value, bool expected)
    {
        string[] variableNames = ["TF_BUILD", "GITHUB_ACTIONS", "APPVEYOR", "CI", "TRAVIS", "CIRCLECI"];
        foreach (var variableName in variableNames)
        {
            var environment = new TestEnvironment(new Dictionary<string, string?> { [variableName] = value });
            var detector = new CIEnvironmentDetector(environment);

            Assert.Equal(expected, detector.IsCIEnvironment());
        }
    }

    [Theory]
    [InlineData("build-123", "value", true)]
    [InlineData("build-123", null, false)]
    [InlineData(null, "value", false)]
    [InlineData("build-123", "", false)]
    [InlineData("", "value", false)]
    [InlineData(" ", " ", true)]
    [InlineData("false", "0", true)]
    public void IsCIEnvironment_RequiresBothPresenceVariables(string? firstValue, string? secondValue, bool expected)
    {
        (string First, string Second)[] variablePairs =
        [
            ("CODEBUILD_BUILD_ID", "AWS_REGION"),
            ("BUILD_ID", "BUILD_URL"),
            ("BUILD_ID", "PROJECT_ID")
        ];
        foreach (var (first, second) in variablePairs)
        {
            var environment = new TestEnvironment(new Dictionary<string, string?>
            {
                [first] = firstValue,
                [second] = secondValue
            });
            var detector = new CIEnvironmentDetector(environment);

            Assert.Equal(expected, detector.IsCIEnvironment());
        }
    }

    [Theory]
    [InlineData("2023.1", true)]
    [InlineData("https://space.example.com", true)]
    [InlineData("false", true)]
    [InlineData("0", true)]
    [InlineData(" ", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCIEnvironment_MatchesPresenceOnlyVariables(string? value, bool expected)
    {
        string[] variableNames = ["TEAMCITY_VERSION", "JB_SPACE_API_URL"];
        foreach (var variableName in variableNames)
        {
            var environment = new TestEnvironment(new Dictionary<string, string?> { [variableName] = value });
            var detector = new CIEnvironmentDetector(environment);

            Assert.Equal(expected, detector.IsCIEnvironment());
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("invalid")]
    public void IsCIEnvironment_DoesNotLetCIFlagOverrideOtherSignals(string ciValue)
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["CI"] = ciValue,
            ["GITHUB_ACTIONS"] = "true"
        });
        var detector = new CIEnvironmentDetector(environment);

        Assert.True(detector.IsCIEnvironment());
    }

    [Theory]
    [InlineData("AZURE_PIPELINES")]
    [InlineData("JENKINS_URL")]
    [InlineData("GITLAB_CI")]
    [InlineData("BUILDKITE")]
    [InlineData("BITBUCKET_BUILD_NUMBER")]
    public void IsCIEnvironment_IgnoresMarkersNotRecognizedByDotNet(string variableName)
    {
        var environment = new TestEnvironment(new Dictionary<string, string?> { [variableName] = "true" });
        var detector = new CIEnvironmentDetector(environment);

        Assert.False(detector.IsCIEnvironment());
    }

    [Theory]
    [InlineData("true", "false", true)]
    [InlineData("false", "true", false)]
    [InlineData(null, "true", false)]
    public void IsCIEnvironment_UsesEnvironmentInsteadOfConfiguration(string? environmentValue, string configurationValue, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CI"] = configurationValue })
            .Build();
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["CI"] = environmentValue });
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IEnvironment>(environment);
        services.AddTelemetryServices();
        using var provider = services.BuildServiceProvider();

        var detector = provider.GetRequiredService<ICIEnvironmentDetector>();

        Assert.Equal(expected, detector.IsCIEnvironment());
    }

    [Fact]
    public void IsCIEnvironment_PreservesCaseSensitiveEnvironmentLookup()
    {
        var environment = new TestEnvironment(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ci"] = "true",
            ["github_actions"] = "true"
        });
        var detector = new CIEnvironmentDetector(environment);

        Assert.False(detector.IsCIEnvironment());
    }
}
