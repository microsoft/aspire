// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Tests.Commands;

public class OperatingSystemCheckTests
{
    [Fact]
    public async Task CheckAsync_ReturnsEnvironmentResultWithMetadata()
    {
        var check = new OperatingSystemCheck(() => new OperatingSystemDetails(
            Type: "Linux",
            Name: "Linux Ubuntu",
            Version: "24.04",
            Description: "Ubuntu 24.04.2 LTS",
            Status: EnvironmentCheckStatus.Pass));

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckCategories.Environment, result.Category);
        Assert.Equal(OperatingSystemCheck.CheckName, result.Name);
        Assert.Equal(EnvironmentCheckStatus.Pass, result.Status);
        Assert.Equal("Operating system: Linux Ubuntu 24.04", result.Message);
        Assert.NotNull(result.Metadata);
        Assert.Equal("Linux", result.Metadata["osType"]!.GetValue<string>());
        Assert.Equal("Linux Ubuntu", result.Metadata["displayName"]!.GetValue<string>());
        Assert.Equal("24.04", result.Metadata["version"]!.GetValue<string>());
        Assert.Equal("Ubuntu 24.04.2 LTS", result.Metadata["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarningForUnknownOperatingSystem()
    {
        var check = new OperatingSystemCheck(() => new OperatingSystemDetails(
            Type: "unknown",
            Name: "unknown",
            Version: "1.2.3",
            Description: "Unknown OS description",
            Status: EnvironmentCheckStatus.Warning,
            MessageDisplayName: DoctorCommandStrings.VersionUnknown));

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal("Operating system: unknown 1.2.3", result.Message);
        Assert.NotNull(result.Metadata);
        Assert.Equal("unknown", result.Metadata["osType"]!.GetValue<string>());
        Assert.Equal("unknown", result.Metadata["displayName"]!.GetValue<string>());
        Assert.Equal("1.2.3", result.Metadata["version"]!.GetValue<string>());
        Assert.Equal("Unknown OS description", result.Metadata["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(10, 0, 19045)]
    [InlineData(10, 0, 22631)]
    public void CreateWindowsDetails_UsesHighLevelWindowsName(int major, int minor, int build)
    {
        var details = OperatingSystemCheck.CreateWindowsDetails(new Version(major, minor, build), "Microsoft Windows");

        Assert.Equal("Windows", details.Type);
        Assert.Equal("Windows", details.Name);
        Assert.Equal($"{major}.{minor}.{build}", details.Version);
    }

    [Theory]
    [InlineData(26, 0)]
    [InlineData(15, 1)]
    public void CreateMacOSDetails_UsesHighLevelMacOSName(int major, int minor)
    {
        var details = OperatingSystemCheck.CreateMacOSDetails(new Version(major, minor), "Darwin");

        Assert.Equal("macOS", details.Type);
        Assert.Equal("macOS", details.Name);
        Assert.Equal($"{major}.{minor}", details.Version);
    }

    [Fact]
    public void ParseLinuxOsRelease_StripsQuotesAndPreservesEscapes()
    {
        var values = OperatingSystemCheck.ParseLinuxOsRelease("""
            NAME="Example \"Linux\""
            PRETTY_NAME="Cost \$1\!"
            VERSION_ID='1.0'
            PATH='path\\to'
            ID=example-linux
            """);

        Assert.Equal("Example \\\"Linux\\\"", values["NAME"]);
        Assert.Equal(@"Cost \$1\!", values["PRETTY_NAME"]);
        Assert.Equal("1.0", values["VERSION_ID"]);
        Assert.Equal(@"path\\to", values["PATH"]);
        Assert.Equal("example-linux", values["ID"]);
    }
}
