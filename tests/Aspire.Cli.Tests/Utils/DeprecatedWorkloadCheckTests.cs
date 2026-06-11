// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Utils;

public class DeprecatedWorkloadCheckTests
{
    [Fact]
    public void IsAspireWorkloadInstalled_WithAspireWorkload_ReturnsTrue()
    {
        var output = """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            aspire                     8.0.0/8.0.100          SDK 8.0.100
            maui                       8.0.0/8.0.100          SDK 8.0.100

            Use `dotnet workload search` to find additional workloads to install.
            """;

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(output);

        Assert.True(result);
    }

    [Fact]
    public void IsAspireWorkloadInstalled_WithoutAspireWorkload_ReturnsFalse()
    {
        var output = """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            maui                       8.0.0/8.0.100          SDK 8.0.100
            android                    33.0.0/8.0.100         SDK 8.0.100

            Use `dotnet workload search` to find additional workloads to install.
            """;

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(output);

        Assert.False(result);
    }

    [Fact]
    public void IsAspireWorkloadInstalled_WithNoWorkloads_ReturnsFalse()
    {
        var output = """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------

            Use `dotnet workload search` to find additional workloads to install.
            """;

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(output);

        Assert.False(result);
    }

    [Fact]
    public void IsAspireWorkloadInstalled_WithEmptyOutput_ReturnsFalse()
    {
        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled("");

        Assert.False(result);
    }

    [Fact]
    public void IsAspireWorkloadInstalled_WithNullOutput_ReturnsFalse()
    {
        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(null!);

        Assert.False(result);
    }

    [Fact]
    public void IsAspireWorkloadInstalled_WithWhitespaceOutput_ReturnsFalse()
    {
        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled("   \n   \n   ");

        Assert.False(result);
    }

    [Fact]
    public void IsAspireWorkloadInstalled_IsCaseInsensitive()
    {
        var output = """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            ASPIRE                     8.0.0/8.0.100          SDK 8.0.100
            """;

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(output);

        Assert.True(result);
    }

    [Fact]
    public void IsAspireWorkloadInstalled_WithAspireAsPartOfOtherName_ReturnsFalse()
    {
        // Ensure we only match the exact workload name, not partial matches
        var output = """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            aspire-components          8.0.0/8.0.100          SDK 8.0.100
            """;

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(output);

        Assert.False(result);
    }

    [Fact]
    public void IsAspireWorkloadInstalled_WithTabSeparatedColumns_ReturnsTrue()
    {
        var output = "Installed Workload Id\tManifest Version\tInstallation Source\n" +
                     "------------------------------------------------------------\n" +
                     "aspire\t8.0.0/8.0.100\tSDK 8.0.100\n";

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(output);

        Assert.True(result);
    }

    /// <summary>
    /// This test validates the exact format from 'dotnet workload list' command.
    /// If this test fails, the output format may have changed and the parser needs updating.
    /// </summary>
    [Fact]
    public void IsAspireWorkloadInstalled_WithRealWorldFormat_ParsesCorrectly()
    {
        // This is the actual output format from 'dotnet workload list' as of .NET 8/9/10
        // The format is: WorkloadId (whitespace) ManifestVersion (whitespace) InstallationSource
        // If Microsoft changes this format, this test should fail and alert us to update the parser
        var realWorldOutput = """
            Workload version: 10.0.101.1

            Installed Workload Id      Manifest Version      Installation Source
            --------------------------------------------------------------------
            aspire                     8.2.2/8.0.100         SDK 8.0.400

            Use `dotnet workload search` to find additional workloads to install.
            """;

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(realWorldOutput);

        Assert.True(result);
    }

    /// <summary>
    /// Validates that newer format with 'Workload version:' header line is handled.
    /// </summary>
    [Fact]
    public void IsAspireWorkloadInstalled_WithWorkloadVersionHeader_ParsesCorrectly()
    {
        var output = """
            Workload version: 8.0.100-manifests.abcd1234

            Installed Workload Id      Manifest Version      Installation Source
            --------------------------------------------------------------------
            aspire                     8.0.0/8.0.100         SDK 8.0.100
            """;

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(output);

        Assert.True(result);
    }

    /// <summary>
    /// Validates that header and metadata lines are properly skipped and don't cause false positives.
    /// </summary>
    [Fact]
    public void IsAspireWorkloadInstalled_WithNoAspireWorkload_SkipsHeaderLines()
    {
        // This tests that "Workload version:", "Installed Workload Id", "Use `dotnet...",
        // and separator lines are all properly skipped when aspire is NOT installed
        var output = """
            Workload version: 10.0.101.1

            Installed Workload Id      Manifest Version      Installation Source
            --------------------------------------------------------------------
            maui                       8.0.0/8.0.100         SDK 8.0.100

            Use `dotnet workload search` to find additional workloads to install.
            """;

        var result = DeprecatedWorkloadCheck.IsAspireWorkloadInstalled(output);

        Assert.False(result);
    }
}
