// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class NpmInstallDetectionTests
{
    [Fact]
    public void IsRunningFromNpm_ReturnsTrueWhenPackageEnvironmentVariableMatches()
    {
        using var scope = NpmInstallDetection.UseEnvironmentForTesting(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = NpmInstallDetection.ExpectedPackageName,
            [NpmInstallDetection.PackageVersionEnvironmentVariableName] = "9.4.0",
            [NpmInstallDetection.PackageRidEnvironmentVariableName] = "linux-x64",
        });

        Assert.True(NpmInstallDetection.IsRunningFromNpm());
        Assert.Equal(
            $"npm install -g {NpmInstallDetection.ExpectedPackageName}@latest",
            NpmInstallDetection.GetNpmUpdateCommand());
        Assert.Equal("9.4.0", NpmInstallDetection.GetNpmPackageVersion());
        Assert.Equal("linux-x64", NpmInstallDetection.GetNpmPackageRid());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@some-other-scope/aspire-cli")]
    [InlineData("aspire-cli")]
    [InlineData("@Microsoft/Aspire-Cli")]
    public void IsRunningFromNpm_ReturnsFalseWhenPackageEnvironmentVariableMissingOrMismatched(string? packageName)
    {
        using var scope = NpmInstallDetection.UseEnvironmentForTesting(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = packageName,
            [NpmInstallDetection.PackageVersionEnvironmentVariableName] = "9.4.0",
            [NpmInstallDetection.PackageRidEnvironmentVariableName] = "linux-x64",
        });

        Assert.False(NpmInstallDetection.IsRunningFromNpm());
        Assert.Null(NpmInstallDetection.GetNpmUpdateCommand());
    }

    [Fact]
    public void GetNpmPackageVersion_ReturnsNullWhenVariableMissing()
    {
        using var scope = NpmInstallDetection.UseEnvironmentForTesting(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = NpmInstallDetection.ExpectedPackageName,
        });

        Assert.Null(NpmInstallDetection.GetNpmPackageVersion());
        Assert.Null(NpmInstallDetection.GetNpmPackageRid());
    }

    [Fact]
    public void UseEnvironmentForTesting_RestoresPreviousValueOnDispose()
    {
        // Establish a known baseline that does NOT match, so the test result is
        // independent of whatever ASPIRE_NPM_PACKAGE the test host may inherit.
        using var baseline = NpmInstallDetection.UseEnvironmentForTesting(new Dictionary<string, string?>());
        Assert.False(NpmInstallDetection.IsRunningFromNpm());

        using (NpmInstallDetection.UseEnvironmentForTesting(new Dictionary<string, string?>
        {
            [NpmInstallDetection.PackageEnvironmentVariableName] = NpmInstallDetection.ExpectedPackageName,
        }))
        {
            Assert.True(NpmInstallDetection.IsRunningFromNpm());
        }

        Assert.False(NpmInstallDetection.IsRunningFromNpm());
    }
}
