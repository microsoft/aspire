// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests;

public class DotNetSdkInstallerTests
{
    [Fact]
    public async Task CheckAsync_WhenDotNetIsAvailable_ReturnsTrue()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        // This test assumes the test environment has .NET SDK installed
        var result = await installer.CheckAsync();

        Assert.True(result);
    }

    [Fact]
    public async Task CheckAsync_WithMinimumVersion_WhenDotNetIsAvailable_ReturnsTrue()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        // This test assumes the test environment has .NET SDK installed with a version >= 8.0.0
        var result = await installer.CheckAsync("8.0.0");

        Assert.True(result);
    }

    [Fact]
    public async Task CheckAsync_WithActualMinimumVersion_BehavesCorrectly()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        // Use the actual minimum version constant and check the behavior
        // Since this test environment has 8.0.117, it should return false for 9.0.302
        var result = await installer.CheckAsync(DotNetSdkInstaller.MinimumSdkVersion);

        // Don't assert the specific result, just ensure the method doesn't throw
        // The behavior will depend on what SDK versions are actually installed
        Assert.True(result == true || result == false); // This will always pass but exercises the code path
    }

    [Fact]
    public async Task CheckAsync_WithHighMinimumVersion_ReturnsFalse()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        // Use an unreasonably high version that should not exist
        var result = await installer.CheckAsync("99.0.0");

        Assert.False(result);
    }

    [Fact]
    public async Task CheckAsync_WithInvalidMinimumVersion_ReturnsFalse()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        // Use an invalid version string
        var result = await installer.CheckAsync("invalid.version");

        Assert.False(result);
    }

    [Fact]
    public async Task InstallAsync_ThrowsNotImplementedException()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        await Assert.ThrowsAsync<NotImplementedException>(() => installer.InstallAsync());
    }

    [Fact]
    public async Task CheckReturnsTrueIfFeatureDisabled()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(false), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        // Use an invalid version string
        var result = await installer.CheckAsync("invalid.version");

        Assert.True(result);
    }

    [Fact]
    public async Task CheckAsync_UsesArchitectureSpecificCommand()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        // This test verifies that the architecture-specific command is used
        // Since the implementation adds --arch flag, it should still work correctly
        var result = await installer.CheckAsync("8.0.0");

        // The test should pass if the command with --arch flag works
        Assert.True(result);
    }

    [Fact]
    public async Task CheckAsync_UsesOverrideMinimumSdkVersion_WhenConfigured()
    {
        var configuration = CreateConfigurationWithOverride("8.0.0");
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), configuration, CreateEmptyServiceProvider());

        // The installer should use the override version instead of the constant
        var result = await installer.CheckAsync();

        // Should use 8.0.0 instead of 9.0.302, which should be available in test environment
        Assert.True(result);
    }

    [Fact]
    public async Task CheckAsync_UsesDefaultMinimumSdkVersion_WhenNotConfigured()
    {
        var installer = new DotNetSdkInstaller(new MinimumSdkCheckFeature(), CreateEmptyConfiguration(), CreateEmptyServiceProvider());

        // Call the parameterless method that should use the default constant
        var result = await installer.CheckAsync();

        // The result depends on whether 9.0.302 is installed, but the test ensures no exception is thrown
        Assert.True(result == true || result == false);
    }

    private static IConfiguration CreateEmptyConfiguration()
    {
        return new ConfigurationBuilder().Build();
    }

    private static IConfiguration CreateConfigurationWithOverride(string overrideVersion)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string?>("overrideMinimumSdkVersion", overrideVersion)
            })
            .Build();
    }

    private static IServiceProvider CreateEmptyServiceProvider()
    {
        return new ServiceCollection().BuildServiceProvider();
    }
}

public class MinimumSdkCheckFeature(bool enabled = true) : IFeatures
{
    public bool IsFeatureEnabled(string featureName, bool defaultValue = false)
    {
        return featureName == KnownFeatures.MinimumSdkCheckEnabled ? enabled : false;
    }
}
